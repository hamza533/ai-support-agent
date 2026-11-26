using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Brightline.Ai;
using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.Knowledge.Search;
using Brightline.LegacyOrders.Client;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Brightline.Agent;

/// <summary>
/// One customer conversation (one per Blazor circuit, or per eval scenario). Runs the Agent Framework agent,
/// turns its tool-approval requests into confirmation cards, and persists the transcript and audit trail.
/// </summary>
public sealed class ChatService(
    IChatClient chatClient,
    ILegacyOrdersClient orders,
    IKnowledgeSearch search,
    IAgentSettingsProvider settingsService,
    IDbContextFactory<AppDbContext> dbFactory,
    IConversationStore store,
    IOptions<DemoOptions> demo,
    IOptions<AiOptions> aiOptions,
    TimeProvider time,
    ILoggerFactory loggerFactory)
{
    public const int MaxMessageLength = 2000;
    public const string ActivitySourceName = "Brightline.Agent";
    private static readonly ActivitySource _activity = new(ActivitySourceName);

    private readonly ILogger _logger = loggerFactory.CreateLogger<ChatService>();
    private readonly Dictionary<string, PendingApproval> _pending = [];
    private AIAgent? _agent;
    private AgentSession? _agentSession;
    private SupportTools? _tools;

    private sealed class PendingApproval(ToolApprovalRequestContent request, ApprovalCard card)
    {
        public ToolApprovalRequestContent Request { get; } = request;

        public ApprovalCard Card { get; } = card;

        public bool? Approved { get; set; }
    }

    public ConversationSession Session { get; } = new();

    public IReadOnlyList<ApprovalCard> PendingApprovals => [.. _pending.Values.Where(p => p.Approved is null).Select(p => p.Card)];

    public async IAsyncEnumerable<ChatEvent> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            yield break;
        }

        if (text.Length > MaxMessageLength)
        {
            yield return new ChatError(ChatErrorKinds.Failed, $"Messages can be up to {MaxMessageLength} characters.");
            yield break;
        }

        if (PendingApprovals.Count > 0)
        {
            yield return new ChatError(ChatErrorKinds.Failed, "Please confirm or cancel the pending action first.");
            yield break;
        }

        if (!Session.IsPersisted && !Session.IsTest && demo.Value.Enabled
            && await store.CountStartedTodayAsync(ct) >= demo.Value.DailyConversations)
        {
            yield return new ChatError(ChatErrorKinds.DailyCap, "The demo has reached today's conversation limit. Please come back tomorrow.");
            yield break;
        }

        await store.AddMessageAsync(Session, MessageRole.User, text, ct: ct);
        Session.BeginTurn();
        await foreach (var e in RunTurnAsync([new ChatMessage(ChatRole.User, text)], ct))
        {
            yield return e;
        }
    }

    /// <summary>The customer pressed Confirm or Cancel on a confirmation card.</summary>
    public async IAsyncEnumerable<ChatEvent> RespondToApprovalAsync(string requestId, bool approved, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(requestId, out var pending) || pending.Approved is not null)
        {
            yield break; // already answered (double click) – nothing to do, so nothing can run twice
        }

        if (!pending.Card.Allowed)
        {
            approved = false; // a blocked card can only be cancelled
        }

        pending.Approved = approved;
        var call = (FunctionCallContent)pending.Request.ToolCall;
        await store.RecordToolCallAsync(Session, new ToolCallRecord(
            call.CallId, call.Name, SupportTools.MaskArgs(call.Arguments ?? new Dictionary<string, object?>()),
            approved ? "approved" : "cancelled", approved ? "approved by customer" : "cancelled by customer",
            approved ? ApprovalState.Approved : ApprovalState.Denied, null, 0), ct);

        if (_pending.Values.Any(p => p.Approved is null))
        {
            yield break; // wait until every card of this turn is answered
        }

        var responses = _pending.Values.Select(p => (AIContent)p.Request.CreateResponse(
            p.Approved == true,
            p.Approved == true ? null : p.Card.Allowed ? "The customer cancelled this action." : $"Blocked by policy: {p.Card.BlockedReason}")).ToList();
        _pending.Clear();
        Session.BeginTurn();
        await foreach (var e in RunTurnAsync([new ChatMessage(ChatRole.User, responses)], ct))
        {
            yield return e;
        }
    }

    private async IAsyncEnumerable<ChatEvent> RunTurnAsync(IList<ChatMessage> input, [EnumeratorCancellation] CancellationToken ct)
    {
        using var activity = _activity.StartActivity("chat turn");
        activity?.SetTag("conversation.id", Session.ConversationId);
        var sw = Stopwatch.StartNew();
        int? ttft = null;
        var text = new StringBuilder();
        var usage = new UsageDetails();
        var approvals = new List<ToolApprovalRequestContent>();
        ChatError? error = null;

        await EnsureAgentAsync(ct);
        var stream = _agent!.RunStreamingAsync(input, _agentSession, cancellationToken: ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        break;
                    }

                    update = stream.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    error = MapError(ex);
                    break;
                }

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent t when !string.IsNullOrEmpty(t.Text):
                            ttft ??= (int)sw.ElapsedMilliseconds;
                            text.Append(t.Text);
                            yield return new TextDelta(t.Text);
                            break;
                        case UsageContent u:
                            usage.Add(u.Details);
                            break;
                        case ToolApprovalRequestContent request when request.ToolCall is FunctionCallContent:
                            approvals.Add(request);
                            break;
                    }
                }

                foreach (var e in Session.DrainEvents())
                {
                    yield return e;
                }
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        foreach (var e in Session.DrainEvents())
        {
            yield return e;
        }

        if (error is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.Kind);
            yield return error;
        }

        // Turn approval requests into server-built confirmation cards.
        foreach (var request in approvals)
        {
            var call = (FunctionCallContent)request.ToolCall;
            var card = await _tools!.DescribeApprovalAsync(request.RequestId, call, ct);
            _pending[request.RequestId] = new PendingApproval(request, card);
            await store.RecordToolCallAsync(Session, new ToolCallRecord(
                call.CallId, call.Name, SupportTools.MaskArgs(call.Arguments ?? new Dictionary<string, object?>()),
                card.Allowed ? "awaiting_confirmation" : "blocked", card.Allowed ? "confirmation card shown" : card.BlockedReason,
                ApprovalState.Pending, null, 0), ct);
            yield return card;
        }

        var tokensIn = usage.InputTokenCount ?? 0;
        var tokensOut = usage.OutputTokenCount ?? 0;
        var raw = text.ToString().Trim();
        if (raw.Length > 0 || tokensIn + tokensOut > 0)
        {
            var (clean, cited) = CitationProcessor.Process(raw, Session.Citations);
            var (markers, validMarkers) = CitationProcessor.Count(raw, Session.Citations);
            var offerTicket = clean.Contains(Prompts.IDontKnowPhrase, StringComparison.OrdinalIgnoreCase);
            await store.AddMessageAsync(Session, MessageRole.Assistant, clean, cited, ttft, tokensIn, tokensOut, aiOptions.Value.ChatModel, CancellationToken.None);
            if (clean.Length > 0)
            {
                yield return new AssistantMessage(clean, cited, ttft, offerTicket, tokensIn, tokensOut, markers, validMarkers);
            }
        }

        activity?.SetTag("tokens.in", tokensIn);
        activity?.SetTag("tokens.out", tokensOut);
        activity?.SetTag("ttft.ms", ttft);
    }

    private async Task EnsureAgentAsync(CancellationToken ct)
    {
        if (_agent is not null)
        {
            return;
        }

        var settings = await settingsService.GetAsync(ct);
        _tools = new SupportTools(Session, orders, search, settings, dbFactory, store, time, loggerFactory.CreateLogger<SupportTools>());
        _agent = new ChatClientAgent(
                chatClient,
                new ChatClientAgentOptions
                {
                    Name = "brightline-support",
                    Description = "Brightline Electronics customer support agent",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = settings.SystemPrompt,
                        Tools = _tools.AsAITools(),
                        Temperature = settings.Temperature,
                        AllowMultipleToolCalls = true,
                    },
                },
                loggerFactory)
            .AsBuilder()
            .UseOpenTelemetry(ActivitySourceName)
            .Build();
        _agentSession = await _agent.CreateSessionAsync(ct);
    }

    private ChatError MapError(Exception ex)
    {
        _logger.LogError(ex, "Chat turn failed for conversation {ConversationId}", Session.ConversationId);
        return ex switch
        {
            AiNotConfiguredException => new ChatError(ChatErrorKinds.AiUnavailable, "The assistant isn't configured yet (no AI provider key). Please contact support."),
            ClientResultException { Status: 429 } => new ChatError(ChatErrorKinds.AiUnavailable, "The assistant is very busy right now. Please try again in a minute."),
            ClientResultException or HttpRequestException or TaskCanceledException => new ChatError(ChatErrorKinds.AiUnavailable, "The assistant is temporarily unavailable. Please try again shortly."),
            _ => new ChatError(ChatErrorKinds.Failed, "Something went wrong. Please try again."),
        };
    }
}
