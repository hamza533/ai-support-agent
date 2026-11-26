using System.Text.Json;
using Brightline.Agent;
using Brightline.Ai;
using Brightline.Data;
using Brightline.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Brightline.Web.Admin;

public sealed record OverviewStats(
    int ConversationsToday,
    double ResolvedWithoutHumanPercent,
    int TicketsToday,
    double AvgFirstResponseSeconds,
    IReadOnlyList<(DateOnly Day, int Count)> PerDay,
    IReadOnlyList<RecentAction> RecentActions,
    IReadOnlyList<Ticket> NeedsAttention);

public sealed record RecentAction(DateTimeOffset At, string Customer, string Action, string Status, Guid ConversationId);

public sealed record ConversationRow(Guid Id, DateTimeOffset StartedAt, string Customer, ConversationStatus Status, bool Verified, bool IsTest, int Messages, string Preview);

public sealed record ConversationDetail(
    Conversation Conversation,
    IReadOnlyList<MessageView> Messages,
    IReadOnlyList<ToolCall> ToolCalls,
    IReadOnlyList<Ticket> Tickets,
    int? AvgTtftMs,
    decimal EstimatedCost);

public sealed record MessageView(Message Message, IReadOnlyList<Citation> Citations);

public enum ConversationFilter
{
    All,
    Escalated,
    Unverified,
    Verified,
    Test,
}

/// <summary>Read models and staff actions for the admin console (short-lived DbContexts, safe in long circuits).</summary>
public sealed class AdminService(IDbContextFactory<AppDbContext> dbFactory, IOptions<AiOptions> ai, TimeProvider time)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<OverviewStats> GetOverviewAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var from = today.AddDays(-13);

        var todays = await db.Conversations.AsNoTracking().Where(c => c.StartedAt >= today && !c.IsTest)
            .Select(c => new { c.Id, c.Status }).ToListAsync(ct);
        var ticketConversations = await db.Tickets.AsNoTracking().Where(t => t.CreatedAt >= today && t.ConversationId != null)
            .Select(t => t.ConversationId!.Value).Distinct().ToListAsync(ct);
        var resolved = todays.Count(c => c.Status != ConversationStatus.Escalated && !ticketConversations.Contains(c.Id));
        var ticketsToday = await db.Tickets.CountAsync(t => t.CreatedAt >= today, ct);
        var ttfts = await db.Messages.AsNoTracking().Where(m => m.CreatedAt >= today && m.TtftMs != null).Select(m => m.TtftMs!.Value).ToListAsync(ct);

        var starts = await db.Conversations.AsNoTracking().Where(c => c.StartedAt >= from && !c.IsTest).Select(c => c.StartedAt).ToListAsync(ct);
        var perDay = Enumerable.Range(0, 14)
            .Select(i => DateOnly.FromDateTime(from.AddDays(i).UtcDateTime))
            .Select(d => (d, starts.Count(s => DateOnly.FromDateTime(s.UtcDateTime) == d)))
            .ToList();

        var actions = await db.ToolCalls.AsNoTracking()
            .Where(t => SupportTools.WriteTools.Contains(t.Tool) || t.Tool == SupportTools.GetOrderStatusName)
            .OrderByDescending(t => t.CreatedAt).Take(8)
            .Join(db.Conversations, t => t.ConversationId, c => c.Id, (t, c) => new { t, c.CustomerLabel })
            .ToListAsync(ct);

        var attention = await db.Tickets.AsNoTracking()
            .Where(t => t.Status != TicketStatus.Resolved && (t.Priority == TicketPriority.High || t.Priority == TicketPriority.Urgent))
            .OrderByDescending(t => t.CreatedAt).Take(6).ToListAsync(ct);

        return new OverviewStats(
            todays.Count,
            todays.Count == 0 ? 0 : Math.Round(100.0 * resolved / todays.Count, 0),
            ticketsToday,
            ttfts.Count == 0 ? 0 : Math.Round(ttfts.Average() / 1000.0, 1),
            perDay,
            [.. actions.Select(a => new RecentAction(a.t.CreatedAt, a.CustomerLabel ?? "Unverified", ActionLabel(a.t), a.t.ResultStatus, a.t.ConversationId))],
            attention);
    }

    public async Task<IReadOnlyList<ConversationRow>> ListConversationsAsync(ConversationFilter filter, string? search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Conversations.AsNoTracking().AsQueryable();
        q = filter switch
        {
            ConversationFilter.Escalated => q.Where(c => c.Status == ConversationStatus.Escalated),
            ConversationFilter.Unverified => q.Where(c => c.VerifiedCustomerId == null && !c.IsTest),
            ConversationFilter.Verified => q.Where(c => c.VerifiedCustomerId != null),
            ConversationFilter.Test => q.Where(c => c.IsTest),
            _ => q,
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            q = q.Where(c => (c.CustomerLabel != null && EF.Functions.ILike(c.CustomerLabel, term))
                || db.Messages.Any(m => m.ConversationId == c.Id && EF.Functions.ILike(m.Text, term)));
        }

        var rows = await q.OrderByDescending(c => c.LastActivityAt).Take(100)
            .Select(c => new
            {
                c.Id,
                c.StartedAt,
                c.CustomerLabel,
                c.Status,
                Verified = c.VerifiedCustomerId != null,
                c.IsTest,
                Messages = db.Messages.Count(m => m.ConversationId == c.Id),
                First = db.Messages.Where(m => m.ConversationId == c.Id && m.Role == MessageRole.User).OrderBy(m => m.Id).Select(m => m.Text).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => new ConversationRow(r.Id, r.StartedAt, r.CustomerLabel ?? "Unverified customer", r.Status, r.Verified, r.IsTest, r.Messages,
            PiiMasker.MaskText(Truncate(r.First, 90))))];
    }

    public async Task<ConversationDetail?> GetConversationAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (conversation is null)
        {
            return null;
        }

        var messages = await db.Messages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Id).ToListAsync(ct);
        var calls = await db.ToolCalls.AsNoTracking().Where(t => t.ConversationId == id).OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync(ct);
        var tickets = await db.Tickets.AsNoTracking().Where(t => t.ConversationId == id).ToListAsync(ct);
        var ttfts = messages.Where(m => m.TtftMs != null).Select(m => m.TtftMs!.Value).ToList();

        return new ConversationDetail(
            conversation,
            [.. messages.Select(m => new MessageView(m, JsonSerializer.Deserialize<List<Citation>>(m.Citations, _json) ?? []))],
            calls,
            tickets,
            ttfts.Count == 0 ? null : (int)ttfts.Average(),
            ai.Value.Pricing.Estimate(conversation.TokensIn, conversation.TokensOut));
    }

    public async Task SetConversationStatusAsync(Guid id, ConversationStatus status, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status), ct);
    }

    public async Task<IReadOnlyList<Ticket>> ListTicketsAsync(TicketStatus? status, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Tickets.AsNoTracking();
        if (status is not null)
        {
            q = q.Where(t => t.Status == status);
        }

        return await q.OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync(ct);
    }

    public async Task<bool> UpdateTicketAsync(int id, TicketStatus status, string? notes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var trimmed = notes is { Length: > 4000 } ? notes[..4000] : notes;
        return await db.Tickets.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, status)
            .SetProperty(t => t.InternalNotes, trimmed)
            .SetProperty(t => t.UpdatedAt, time.GetUtcNow()), ct) > 0;
    }

    public async Task<IReadOnlyList<Source>> ListDocumentsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sources.AsNoTracking().OrderBy(s => s.Title)
            .Select(s => new Source
            {
                Id = s.Id,
                Title = s.Title,
                Uri = s.Uri,
                FileName = s.FileName,
                Type = s.Type,
                Status = s.Status,
                ChunkCount = s.ChunkCount,
                ActiveVersion = s.ActiveVersion,
                Error = s.Error,
                UpdatedAt = s.UpdatedAt,
                CreatedAt = s.CreatedAt,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Job>> ListJobsAsync(int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Jobs.AsNoTracking().OrderByDescending(j => j.Id).Take(take).ToListAsync(ct);
    }

    public static string ActionLabel(ToolCall call) => (call.Tool, call.ResultStatus) switch
    {
        (SupportTools.CreateReturnRequestName, "ok") => $"Return request created {call.ResultSummary?.Split(' ')[0]}",
        (SupportTools.CreateReturnRequestName, "cancelled") => "Return request cancelled by customer",
        (SupportTools.CreateReturnRequestName, _) => "Return request " + call.ResultStatus.Replace('_', ' '),
        (SupportTools.CreateSupportTicketName, "ok") => $"Support ticket created {call.ResultSummary?.Split(' ')[0]}",
        (SupportTools.CreateSupportTicketName, _) => "Support ticket " + call.ResultStatus.Replace('_', ' '),
        (SupportTools.EscalateToHumanName, _) => "Escalated to a person",
        (SupportTools.GetOrderStatusName, "ok") => "Customer verified",
        (SupportTools.GetOrderStatusName, _) => "Verification " + call.ResultStatus.Replace('_', ' '),
        _ => SupportTools.ToolLabel(call.Tool),
    };

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";
}
