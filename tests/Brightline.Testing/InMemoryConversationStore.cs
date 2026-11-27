using Brightline.Agent;
using Brightline.Data.Entities;

namespace Brightline.Testing;

/// <summary>Conversation store for unit tests: keeps everything in lists.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    public List<(MessageRole Role, string Text, IReadOnlyList<Citation> Citations)> Messages { get; } = [];

    public Dictionary<string, ToolCallRecord> ToolCalls { get; } = [];

    public ConversationStatus Status { get; private set; } = ConversationStatus.Active;

    public int StartedToday { get; set; }

    public Task EnsurePersistedAsync(ConversationSession session, CancellationToken ct = default)
    {
        session.IsPersisted = true;
        return Task.CompletedTask;
    }

    public Task<int> CountStartedTodayAsync(CancellationToken ct = default) => Task.FromResult(StartedToday);

    public Task MarkVerifiedAsync(ConversationSession session, CancellationToken ct = default) => Task.CompletedTask;

    public Task AddMessageAsync(ConversationSession session, MessageRole role, string text, IReadOnlyList<Citation>? citations = null, int? ttftMs = null, long tokensIn = 0, long tokensOut = 0, string? model = null, CancellationToken ct = default)
    {
        session.IsPersisted = true;
        Messages.Add((role, text, citations ?? []));
        return Task.CompletedTask;
    }

    public Task RecordToolCallAsync(ConversationSession session, ToolCallRecord record, CancellationToken ct = default)
    {
        if (ToolCalls.TryGetValue(record.CallId, out var existing) && record.Approval == ApprovalState.NotRequired)
        {
            record = record with { Approval = existing.Approval };
        }

        ToolCalls[record.CallId] = record;
        return Task.CompletedTask;
    }

    public long TokensIn { get; private set; }

    public Task AddUsageAsync(ConversationSession session, long tokensIn, long tokensOut, string? model, CancellationToken ct = default)
    {
        TokensIn += tokensIn;
        return Task.CompletedTask;
    }

    public Task SetStatusAsync(Guid conversationId, ConversationStatus status, CancellationToken ct = default)
    {
        Status = status;
        return Task.CompletedTask;
    }
}
