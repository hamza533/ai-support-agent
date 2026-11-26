using Brightline.LegacyOrders.Client;

namespace Brightline.Agent;

/// <summary>Events streamed from the agent to the UI (and to tests / the eval harness).</summary>
public abstract record ChatEvent;

public sealed record TextDelta(string Text) : ChatEvent;

public sealed record ToolActivity(string CallId, string Tool, string Label, string Status) : ChatEvent;

public sealed record CustomerVerified(string FirstName) : ChatEvent;

public sealed record OrderCard(OrderDetail Order) : ChatEvent;

/// <summary>The confirmation card for a write tool, built by the server from validated arguments.</summary>
public sealed record ApprovalCard(
    string RequestId,
    string Tool,
    string Title,
    IReadOnlyList<string> Lines,
    string Note,
    bool Allowed,
    string? BlockedReason) : ChatEvent;

public sealed record ActionCompleted(string Kind, string Reference, string Message) : ChatEvent;

public sealed record AssistantMessage(
    string Text,
    IReadOnlyList<Citation> Citations,
    int? TtftMs,
    bool OfferTicket,
    long TokensIn,
    long TokensOut,
    int CitationMarkers = 0,
    int ValidCitationMarkers = 0) : ChatEvent;

public sealed record ChatError(string Kind, string Message) : ChatEvent;

public static class ChatErrorKinds
{
    public const string RateLimited = "rate_limited";
    public const string DailyCap = "daily_cap";
    public const string AiUnavailable = "ai_unavailable";
    public const string Failed = "failed";
}
