using Microsoft.AspNetCore.Identity;

namespace Brightline.Data.Entities;

public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Staff = "staff";
    public const string StaffOrAdmin = Admin + "," + Staff;
}

public enum SourceType
{
    Markdown,
    Pdf,
    Docx,
}

public enum SourceStatus
{
    Processing,
    Indexed,
    Failed,
}

/// <summary>A help-centre document. Chunks of the active version are searchable.</summary>
public sealed class Source
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SourceType Type { get; set; }
    public required string Title { get; set; }
    public required string Uri { get; set; }
    public required string FileName { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public int ActiveVersion { get; set; }
    public int ChunkCount { get; set; }
    public SourceStatus Status { get; set; } = SourceStatus.Processing;
    public string? Error { get; set; }

    /// <summary>Raw uploaded bytes. Small help-centre docs only (max upload size enforced); production would use Blob Storage.</summary>
    public byte[] Content { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF-side view of a chunk. The <c>embedding</c> and <c>tsv</c> columns are managed by SQL / the vector store.</summary>
public sealed class Chunk
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public int ChunkIndex { get; set; }
    public required string Title { get; set; }
    public required string Text { get; set; }
    public string HeadingPath { get; set; } = string.Empty;
    public int? Page { get; set; }
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Dead,
}

public sealed class Job
{
    public long Id { get; set; }
    public required string Kind { get; set; }
    public string Payload { get; set; } = "{}";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Progress { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum ConversationStatus
{
    Active,
    Resolved,
    Escalated,
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int? VerifiedCustomerId { get; set; }

    /// <summary>Masked label for staff views, e.g. "m***@example.com".</summary>
    public string? CustomerLabel { get; set; }
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public string? Summary { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public string? Model { get; set; }
    public bool IsTest { get; set; }
    public List<Message> Messages { get; set; } = [];
    public List<ToolCall> ToolCalls { get; set; } = [];
}

public enum MessageRole
{
    User,
    Assistant,
    System,
}

public sealed class Message
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public MessageRole Role { get; set; }
    public required string Text { get; set; }

    /// <summary>JSON array of <c>{n, sourceId, title, uri, chunkId}</c>.</summary>
    public string Citations { get; set; } = "[]";
    public int? TtftMs { get; set; }
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum ApprovalState
{
    NotRequired,
    Pending,
    Approved,
    Denied,
}

public sealed class ToolCall
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public string CallId { get; set; } = string.Empty;
    public required string Tool { get; set; }

    /// <summary>Arguments as JSON with personal data masked.</summary>
    public string Args { get; set; } = "{}";
    public string ResultStatus { get; set; } = "pending";
    public string? ResultSummary { get; set; }
    public ApprovalState Approval { get; set; }
    public string? IdempotencyKey { get; set; }
    public int LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum TicketPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum TicketStatus
{
    Open,
    InProgress,
    Resolved,
}

public sealed class Ticket
{
    public int Id { get; set; }

    /// <summary>Assigned by the database (TK-####); left null so EF uses the column default.</summary>
    public string Number { get; set; } = null!;
    public Guid? ConversationId { get; set; }
    public required string Category { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public required string Subject { get; set; }
    public required string Summary { get; set; }
    public string? CustomerLabel { get; set; }
    public string? OrderNumber { get; set; }
    public string? ReturnNumber { get; set; }
    public string? InternalNotes { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
