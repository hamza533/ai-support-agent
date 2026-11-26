using System.Globalization;
using System.Text;
using System.Text.Json;
using Brightline.Ai;

namespace Brightline.Eval;

/// <summary>Aggregates and writes eval/results.json and eval/results.md (committed; quoted in the README).</summary>
public sealed class EvalReport
{
    private static readonly CultureInfo _inv = CultureInfo.InvariantCulture;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string JudgeModel { get; set; } = string.Empty;

    public PricingOptions Pricing { get; set; } = new();

    public List<QualityResult> Quality { get; set; } = [];

    public List<UnanswerableResult> Unanswerable { get; set; } = [];

    public List<ScenarioResult> Scenarios { get; set; } = [];

    public double? AvgGroundedness => Avg(Quality.Select(q => q.Groundedness));

    public double? AvgRelevance => Avg(Quality.Select(q => q.Relevance));

    public double? AvgCompleteness => Avg(Quality.Select(q => q.Completeness));

    /// <summary>Share of [n] markers the model wrote that pointed at a source retrieved in that turn (before cleanup).</summary>
    public double CitationValidity => Ratio(Quality.Sum(q => q.ValidCitationMarkers), Quality.Sum(q => q.CitationMarkers));

    /// <summary>Share of answerable questions answered with at least one valid citation.</summary>
    public double AnsweredWithCitation => Ratio(Quality.Count(q => q.Citations > 0), Quality.Count);

    public double CitedExpectedSource => Ratio(Quality.Count(q => q.CitedExpectedSource), Quality.Count);

    public double IDontKnowAccuracy => Ratio(Unanswerable.Count(u => u.SaidIDontKnow), Unanswerable.Count);

    public double TicketOfferRate => Ratio(Unanswerable.Count(u => u.OfferedTicket), Unanswerable.Count);

    public int FalseIDontKnow => Quality.Count(q => q.SaidIDontKnow);

    public int ScenariosPassed => Scenarios.Count(s => s.Passed);

    public double VerificationEnforcement => 1 - Ratio(Scenarios.Sum(s => s.VerificationViolations), Math.Max(1, Scenarios.Sum(s => s.OrderToolCalls)));

    public double ApprovalBeforeWrites => 1 - Ratio(Scenarios.Sum(s => s.ApprovalViolations), Math.Max(1, Scenarios.Sum(s => s.WriteCalls)));

    public int Leaks => Scenarios.Sum(s => s.Leaks);

    public int Duplicates => Scenarios.Sum(s => s.Duplicates);

    private List<int> AllTtft => [.. Quality.Where(q => q.TtftMs is not null).Select(q => q.TtftMs!.Value).Concat(Scenarios.SelectMany(s => s.TtftMs)).Order()];

    public double? TtftP50 => Percentile(AllTtft, 0.50);

    public double? TtftP95 => Percentile(AllTtft, 0.95);

    public double TokensPerConversation
    {
        get
        {
            var conversations = Quality.Select(q => q.TokensIn + q.TokensOut).Concat(Scenarios.Select(s => s.TokensIn + s.TokensOut)).ToList();
            return conversations.Count == 0 ? 0 : conversations.Average();
        }
    }

    public decimal CostPerConversation
    {
        get
        {
            var all = Quality.Select(q => (q.TokensIn, q.TokensOut)).Concat(Scenarios.Select(s => (s.TokensIn, s.TokensOut))).ToList();
            return all.Count == 0 ? 0 : all.Average(x => Pricing.Estimate(x.TokensIn, x.TokensOut));
        }
    }

    public bool MeetsTargets =>
        (Scenarios.Count == 0 || (VerificationEnforcement >= 1 && ApprovalBeforeWrites >= 1 && Leaks == 0 && Duplicates == 0))
        && (Quality.Count == 0 || CitationValidity >= 1);

    public async Task WriteAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new
        {
            StartedAt,
            FinishedAt,
            Provider,
            Model,
            JudgeModel,
            summary = new
            {
                AvgGroundedness,
                AvgRelevance,
                AvgCompleteness,
                CitationValidity,
                AnsweredWithCitation,
                CitedExpectedSource,
                IDontKnowAccuracy,
                TicketOfferRate,
                FalseIDontKnow,
                scenarios = $"{ScenariosPassed}/{Scenarios.Count}",
                VerificationEnforcement,
                ApprovalBeforeWrites,
                Leaks,
                Duplicates,
                TtftP50Ms = TtftP50,
                TtftP95Ms = TtftP95,
                TokensPerConversation,
                EstimatedAzureOpenAiCostPerConversationUsd = CostPerConversation,
                Pricing,
            },
            Quality,
            Unanswerable,
            Scenarios,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(directory, "results.json"), json);
        await File.WriteAllTextAsync(Path.Combine(directory, "results.md"), Markdown());
    }

    public string Summary() =>
        $"""

        Quality ({Quality.Count} questions): groundedness {F(AvgGroundedness)}/5, relevance {F(AvgRelevance)}/5, completeness {F(AvgCompleteness)}/5, citation validity {P(CitationValidity)}
        Unanswerable ({Unanswerable.Count}): "I don't know" {P(IDontKnowAccuracy)}, ticket offered {P(TicketOfferRate)}
        Scenarios: {ScenariosPassed}/{Scenarios.Count} passed · verification {P(VerificationEnforcement)} · approvals {P(ApprovalBeforeWrites)} · leaks {Leaks} · duplicates {Duplicates}
        Performance: TTFT p50 {Ms(TtftP50)} · p95 {Ms(TtftP95)} · {TokensPerConversation:0} tokens/conversation · ~${CostPerConversation.ToString("0.0000", _inv)}/conversation (Azure OpenAI list price)
        Wrote eval/results.md and eval/results.json
        """;

    private string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Evaluation results").AppendLine();
        sb.AppendLine($"Run {StartedAt.ToString("yyyy-MM-dd HH:mm", _inv)} UTC · agent model `{Model}` ({Provider}) · judge `{JudgeModel}` · produced by `make eval` (tests/Brightline.Eval).").AppendLine();
        sb.AppendLine("## Summary").AppendLine();
        sb.AppendLine("| Metric | Result | Target |").AppendLine("|---|---|---|");
        sb.AppendLine($"| Groundedness (AI.Evaluation, 1–5) | {F(AvgGroundedness)} | ≥ 4 |");
        sb.AppendLine($"| Relevance (1–5) | {F(AvgRelevance)} | ≥ 4 |");
        sb.AppendLine($"| Completeness vs reference (1–5) | {F(AvgCompleteness)} | ≥ 4 |");
        sb.AppendLine($"| Citation validity | {P(CitationValidity)} | 100% |");
        sb.AppendLine($"| Answers with ≥1 citation | {P(AnsweredWithCitation)} | – |");
        sb.AppendLine($"| Cited the expected article | {P(CitedExpectedSource)} | – |");
        sb.AppendLine($"| \"I don't know\" on unanswerable ({Unanswerable.Count}) | {P(IDontKnowAccuracy)} | 100% |");
        sb.AppendLine($"| Ticket offered on unanswerable | {P(TicketOfferRate)} | – |");
        sb.AppendLine($"| False \"I don't know\" on answerable | {FalseIDontKnow} | 0 |");
        sb.AppendLine($"| Agent scenarios passed | {ScenariosPassed}/{Scenarios.Count} | {Scenarios.Count}/{Scenarios.Count} |");
        sb.AppendLine($"| Verification enforced on order tools | {P(VerificationEnforcement)} | 100% |");
        sb.AppendLine($"| Approval before writes | {P(ApprovalBeforeWrites)} | 100% |");
        sb.AppendLine($"| Cross-customer leaks | {Leaks} | 0 |");
        sb.AppendLine($"| Duplicate returns/tickets | {Duplicates} | 0 |");
        sb.AppendLine($"| Time to first token p50 / p95 | {Ms(TtftP50)} / {Ms(TtftP95)} | – |");
        sb.AppendLine($"| Tokens per conversation | {TokensPerConversation:0} | – |");
        sb.AppendLine($"| Est. Azure OpenAI cost per conversation | ${CostPerConversation.ToString("0.0000", _inv)} | – |");
        sb.AppendLine().AppendLine("## Agent scenarios").AppendLine();
        sb.AppendLine("| Id | Scenario | Category | Result | Notes |").AppendLine("|---|---|---|---|---|");
        foreach (var s in Scenarios)
        {
            sb.AppendLine($"| {s.Id} | {s.Name} | {s.Category} | {(s.Passed ? "✅" : "❌")} | {Escape(string.Join("; ", s.Failures))} |");
        }

        sb.AppendLine().AppendLine("## Quality questions").AppendLine();
        sb.AppendLine("| Id | Question | Grounded | Relevant | Complete | Citations |").AppendLine("|---|---|---|---|---|---|");
        foreach (var q in Quality)
        {
            sb.AppendLine($"| {q.Id} | {Escape(q.Question)} | {F(q.Groundedness)} | {F(q.Relevance)} | {F(q.Completeness)} | {q.Citations}{(q.Error is null ? string.Empty : " ⚠ " + Escape(q.Error))} |");
        }

        sb.AppendLine().AppendLine("## Unanswerable questions").AppendLine();
        sb.AppendLine("| Id | Question | Said \"I don't know\" | Offered ticket |").AppendLine("|---|---|---|---|");
        foreach (var u in Unanswerable)
        {
            sb.AppendLine($"| {u.Id} | {Escape(u.Question)} | {(u.SaidIDontKnow ? "✅" : "❌")} | {(u.OfferedTicket ? "✅" : "❌")} |");
        }

        return sb.ToString();
    }

    public static double? Percentile(IReadOnlyList<int> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 2);
    }

    private static double Ratio(double part, double total) => total <= 0 ? 0 : part / total;

    private static string F(double? v) => v is null ? "–" : v.Value.ToString("0.00", _inv);

    private static string P(double v) => (v * 100).ToString("0.#", _inv) + "%";

    private static string Ms(double? v) => v is null ? "–" : (v.Value / 1000).ToString("0.00", _inv) + " s";

    private static string Escape(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
