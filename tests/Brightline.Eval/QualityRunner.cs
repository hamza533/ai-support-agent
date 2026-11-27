using Brightline.Agent;
using Brightline.Ai;
using Brightline.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.Eval;

/// <summary>
/// Asks each question through the real agent (search tool + citations), then scores the answer with
/// Microsoft.Extensions.AI.Evaluation quality evaluators. Judge responses are cached in eval/.storage.
/// </summary>
public sealed class QualityRunner
{
    private readonly IServiceProvider _sp;
    private readonly ReportingConfiguration _reporting;

    public QualityRunner(IServiceProvider sp, IConfiguration config, string storagePath)
    {
        _sp = sp;
        var ai = config.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
        var judgeOptions = new AiOptions
        {
            Provider = ai.Provider,
            ChatModel = config["Eval:JudgeModel"] ?? ai.ChatModel,
            Gemini = ai.Gemini,
            AzureOpenAI = ai.AzureOpenAI,
            OpenAI = ai.OpenAI,
            MaxRetries = 6,
        };
        IChatClient judge = new ChatClientBuilder(AiServiceCollectionExtensions.CreateChatClient(judgeOptions))
            .Use(inner => new ThrottledJudge(inner, int.TryParse(config["Eval:JudgeMaxRequestsPerMinute"], out var rpm) ? rpm : 12))
            .Build();

        _reporting = DiskBasedReportingConfiguration.Create(
            storageRootPath: storagePath,
            evaluators: [new GroundednessEvaluator(), new RelevanceEvaluator(), new CompletenessEvaluator()],
            chatConfiguration: new ChatConfiguration(judge),
            enableResponseCaching: true,
            executionName: DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<List<QualityResult>> RunAsync(IReadOnlyList<AnswerableQuestion> questions)
    {
        var results = new List<QualityResult>();
        foreach (var q in questions)
        {
            Console.WriteLine($"[quality] {q.Id} {q.Question}");
            results.Add(await RunOneAsync(q));
        }

        return results;
    }

    public async Task<List<UnanswerableResult>> RunUnanswerableAsync(IReadOnlyList<UnanswerableQuestion> questions)
    {
        var results = new List<UnanswerableResult>();
        foreach (var q in questions)
        {
            Console.WriteLine($"[unanswerable] {q.Id} {q.Question}");
            var (answer, message, error) = await AskAsync(q.Question);
            var idk = answer.Contains(Prompts.IDontKnowPhrase, StringComparison.OrdinalIgnoreCase)
                || answer.Contains("I do not know", StringComparison.OrdinalIgnoreCase);
            results.Add(new UnanswerableResult(q.Id, q.Question, answer, idk, answer.Contains("ticket", StringComparison.OrdinalIgnoreCase) || message?.OfferTicket == true, error));
        }

        return results;
    }

    private async Task<QualityResult> RunOneAsync(AnswerableQuestion q)
    {
        var (answer, message, error) = await AskAsync(q.Question);
        if (message is null)
        {
            return new QualityResult(q.Id, q.Question, answer, null, null, null, 0, 0, 0, false, false, null, 0, 0, error ?? "no answer");
        }

        var context = await LoadContextAsync(message.Citations);
        await using var run = await _reporting.CreateScenarioRunAsync($"quality.{q.Id}");
        var result = await run.EvaluateAsync(
            new ChatMessage(ChatRole.User, q.Question),
            new ChatMessage(ChatRole.Assistant, answer),
            [new GroundednessEvaluatorContext(context.Length > 0 ? context : "(no sources were cited)"), new CompletenessEvaluatorContext(q.Reference)]);

        return new QualityResult(
            q.Id,
            q.Question,
            answer,
            Score(result, GroundednessEvaluator.GroundednessMetricName),
            Score(result, RelevanceEvaluator.RelevanceMetricName),
            Score(result, CompletenessEvaluator.CompletenessMetricName),
            message.Citations.Count,
            message.CitationMarkers,
            message.ValidCitationMarkers,
            message.Citations.Any(c => c.Uri.Length > 0) && await CitesSourceAsync(message.Citations, q.Source),
            answer.Contains(Prompts.IDontKnowPhrase, StringComparison.OrdinalIgnoreCase),
            message.TtftMs,
            message.TokensIn,
            message.TokensOut,
            error);
    }

    private async Task<(string Answer, AssistantMessage? Message, string? Error)> AskAsync(string question)
    {
        await using var scope = _sp.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
        chat.Session.IsTest = true;
        AssistantMessage? last = null;
        string? error = null;
        var queuedBefore = ModelLatency.Queued(scope.ServiceProvider);
        await foreach (var e in chat.SendAsync(question))
        {
            switch (e)
            {
                case AssistantMessage m:
                    last = m;
                    break;
                case ChatError err:
                    error = $"{err.Kind}: {err.Message}";
                    break;
            }
        }

        last = last is null ? null : last with { TtftMs = ModelLatency.Exclude(last.TtftMs, queuedBefore, ModelLatency.Queued(scope.ServiceProvider)) };
        return (last?.Text ?? string.Empty, last, error);
    }

    private async Task<string> LoadContextAsync(IReadOnlyList<Citation> citations)
    {
        if (citations.Count == 0)
        {
            return string.Empty;
        }

        await using var scope = _sp.CreateAsyncScope();
        var ids = citations.Select(c => c.ChunkId).ToList();
        var texts = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id)).Select(c => c.Title + " — " + c.HeadingPath + "\n" + c.Text).ToListAsync();
        return string.Join("\n\n---\n\n", texts);
    }

    private async Task<bool> CitesSourceAsync(IReadOnlyList<Citation> citations, string fileName)
    {
        await using var scope = _sp.CreateAsyncScope();
        var ids = citations.Select(c => c.SourceId).ToList();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sources.AnyAsync(s => ids.Contains(s.Id) && s.FileName == fileName);
    }

    private static double? Score(EvaluationResult result, string name) =>
        result.TryGet<NumericMetric>(name, out var metric) ? metric.Value : null;

    /// <summary>Spaces out judge calls for free-tier rate limits (cached calls don't reach it).</summary>
    private sealed class ThrottledJudge(IChatClient inner, int perMinute) : DelegatingChatClient(inner)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTimeOffset _next = DateTimeOffset.MinValue;

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var wait = _next - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }

                _next = DateTimeOffset.UtcNow.AddSeconds(60.0 / Math.Max(1, perMinute));
            }
            finally
            {
                _gate.Release();
            }

            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
