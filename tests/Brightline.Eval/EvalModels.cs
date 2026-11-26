using System.Text.Json.Serialization;

namespace Brightline.Eval;

public sealed record QuestionSet(List<AnswerableQuestion> Answerable, List<UnanswerableQuestion> Unanswerable);

public sealed record AnswerableQuestion(string Id, string Question, string Reference, string Source);

public sealed record UnanswerableQuestion(string Id, string Question);

public sealed record Scenario(string Id, string Name, string Category, List<ScenarioStep> Steps, ScenarioExpectation Expect, bool LegacyDown = false);

public sealed record ScenarioStep(string? User, bool? Approve);

public sealed record ScenarioExpectation
{
    public List<string> ToolsCalled { get; init; } = [];
    public List<string> ToolsNotCalled { get; init; } = [];
    public List<string> MustContain { get; init; } = [];
    public List<string> MustContainAny { get; init; } = [];
    public List<string> Forbidden { get; init; } = [];
    public bool? Verified { get; init; }
    public int? ApprovalCards { get; init; }
    public int? ReturnsCreated { get; init; }
    public int? TicketsCreated { get; init; }
    public int? MaxReturnQuantity { get; init; }
}

public sealed record QualityResult(
    string Id,
    string Question,
    string Answer,
    double? Groundedness,
    double? Relevance,
    double? Completeness,
    int Citations,
    int CitationMarkers,
    int ValidCitationMarkers,
    bool CitedExpectedSource,
    bool SaidIDontKnow,
    int? TtftMs,
    long TokensIn,
    long TokensOut,
    string? Error);

public sealed record UnanswerableResult(string Id, string Question, string Answer, bool SaidIDontKnow, bool OfferedTicket, string? Error);

public sealed record ScenarioResult(
    string Id,
    string Name,
    string Category,
    bool Passed,
    List<string> Failures,
    List<string> ToolsCalled,
    int OrderToolCalls,
    int VerificationViolations,
    int WriteCalls,
    int ApprovalViolations,
    int Leaks,
    int Duplicates,
    int ReturnsCreated,
    int TicketsCreated,
    List<int> TtftMs,
    long TokensIn,
    long TokensOut,
    [property: JsonIgnore] List<string> Transcript);
