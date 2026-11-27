using System.Text.Json;
using Brightline.Data;
using Brightline.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Brightline.Agent;

public sealed record ToolCallRecord(
    string CallId,
    string Tool,
    string MaskedArgs,
    string ResultStatus,
    string? ResultSummary,
    ApprovalState Approval,
    string? IdempotencyKey,
    int LatencyMs);

public interface IConversationStore
{
    Task EnsurePersistedAsync(ConversationSession session, CancellationToken ct = default);

    Task<int> CountStartedTodayAsync(CancellationToken ct = default);

    Task MarkVerifiedAsync(ConversationSession session, CancellationToken ct = default);

    Task AddMessageAsync(ConversationSession session, MessageRole role, string text, IReadOnlyList<Citation>? citations = null, int? ttftMs = null, long tokensIn = 0, long tokensOut = 0, string? model = null, CancellationToken ct = default);

    Task RecordToolCallAsync(ConversationSession session, ToolCallRecord record, CancellationToken ct = default);

    Task SetStatusAsync(Guid conversationId, ConversationStatus status, CancellationToken ct = default);

    /// <summary>Adds token usage for a turn that produced no visible text (e.g. it ended at a confirmation card).</summary>
    Task AddUsageAsync(ConversationSession session, long tokensIn, long tokensOut, string? model, CancellationToken ct = default);
}

/// <summary>Persists conversations, messages and tool calls (short-lived contexts: safe for long Blazor circuits).</summary>
public sealed class ConversationStore(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time) : IConversationStore
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task EnsurePersistedAsync(ConversationSession session, CancellationToken ct = default)
    {
        if (session.IsPersisted)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Conversations.AnyAsync(c => c.Id == session.ConversationId, ct))
        {
            var now = time.GetUtcNow();
            db.Conversations.Add(new Conversation
            {
                Id = session.ConversationId,
                StartedAt = now,
                LastActivityAt = now,
                IsTest = session.IsTest,
            });
            await db.SaveChangesAsync(ct);
        }

        session.IsPersisted = true;
    }

    public async Task<int> CountStartedTodayAsync(CancellationToken ct = default)
    {
        var start = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Conversations.CountAsync(c => c.StartedAt >= start && !c.IsTest, ct);
    }

    public async Task MarkVerifiedAsync(ConversationSession session, CancellationToken ct = default)
    {
        await EnsurePersistedAsync(session, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == session.ConversationId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.VerifiedCustomerId, session.VerifiedCustomerId)
            .SetProperty(c => c.CustomerLabel, session.VerifiedEmailMasked), ct);
    }

    public async Task AddMessageAsync(ConversationSession session, MessageRole role, string text, IReadOnlyList<Citation>? citations = null, int? ttftMs = null, long tokensIn = 0, long tokensOut = 0, string? model = null, CancellationToken ct = default)
    {
        await EnsurePersistedAsync(session, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        db.Messages.Add(new Message
        {
            ConversationId = session.ConversationId,
            Role = role,
            Text = text,
            Citations = JsonSerializer.Serialize(citations ?? [], _json),
            TtftMs = ttftMs,
            TokensIn = tokensIn > 0 ? (int)tokensIn : null,
            TokensOut = tokensOut > 0 ? (int)tokensOut : null,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await db.Conversations.Where(c => c.Id == session.ConversationId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.LastActivityAt, now)
            .SetProperty(c => c.TokensIn, c => c.TokensIn + tokensIn)
            .SetProperty(c => c.TokensOut, c => c.TokensOut + tokensOut)
            .SetProperty(c => c.Model, c => model ?? c.Model), ct);
    }

    /// <summary>Inserts or updates the audit row for one tool call (keyed by the model's call id).</summary>
    public async Task RecordToolCallAsync(ConversationSession session, ToolCallRecord record, CancellationToken ct = default)
    {
        await EnsurePersistedAsync(session, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ToolCalls.FirstOrDefaultAsync(t => t.ConversationId == session.ConversationId && t.CallId == record.CallId, ct);
        if (row is null)
        {
            row = new ToolCall { ConversationId = session.ConversationId, CallId = record.CallId, Tool = record.Tool, CreatedAt = time.GetUtcNow() };
            db.ToolCalls.Add(row);
        }

        row.Args = record.MaskedArgs;
        row.ResultStatus = record.ResultStatus;
        row.ResultSummary = record.ResultSummary;
        row.LatencyMs = record.LatencyMs;
        row.IdempotencyKey = record.IdempotencyKey ?? row.IdempotencyKey;
        // Keep the strongest approval information: a row created as Pending/Denied by the chat service stays so.
        if (record.Approval != ApprovalState.NotRequired || row.Approval == ApprovalState.NotRequired)
        {
            row.Approval = record.Approval;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AddUsageAsync(ConversationSession session, long tokensIn, long tokensOut, string? model, CancellationToken ct = default)
    {
        await EnsurePersistedAsync(session, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == session.ConversationId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.TokensIn, c => c.TokensIn + tokensIn)
            .SetProperty(c => c.TokensOut, c => c.TokensOut + tokensOut)
            .SetProperty(c => c.Model, c => model ?? c.Model), ct);
    }

    public async Task SetStatusAsync(Guid conversationId, ConversationStatus status, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status), ct);
    }
}
