using Brightline.Agent;
using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.IntegrationTests.App;
using Brightline.LegacyOrders.Client;
using Brightline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Brightline.Testing.ScriptedChatClient;

namespace Brightline.IntegrationTests.Agent;

public sealed class ChatServiceTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private (ChatService Chat, AsyncServiceScope Scope) Create(IChatClient model, FakeLegacyOrdersClient? orders = null)
    {
        var scope = fixture.Factory.Services.CreateAsyncScope();
        var chat = ActivatorUtilities.CreateInstance<ChatService>(scope.ServiceProvider, model, (ILegacyOrdersClient)(orders ?? new FakeLegacyOrdersClient()));
        return (chat, scope);
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task Support_ticket_is_created_only_after_confirmation_and_never_twice()
    {
        var ticketArgs = Args(("subject", "Aurora headphones: left ear cuts out"), ("category", "warranty"), ("priority", "normal"),
            ("summary", "Customer maya.t@example.com reports the left ear cuts out."), ("orderNumber", "BL-10482"));
        var model = new ScriptedChatClient(
            Invoke(SupportTools.GetOrderStatusName, Args(("orderNumber", "BL-10482"), ("email", FakeLegacyOrdersClient.MayaEmail))),
            Invoke(SupportTools.CreateSupportTicketName, ticketArgs),
            messages => Text(LastToolResult(messages) ?? string.Empty),
            Invoke(SupportTools.CreateSupportTicketName, ticketArgs),
            messages => Text(LastToolResult(messages) ?? string.Empty));
        var (chat, scope) = Create(model);
        await using var _ = scope;

        var card = (await AgentHarness.CollectAsync(chat.SendAsync("open a ticket for my headphones", Ct))).OfType<ApprovalCard>().Single();
        Assert.Equal("Confirm support ticket", card.Title);
        Assert.Equal(0, await DbAsync(db => db.Tickets.CountAsync(t => t.ConversationId == chat.Session.ConversationId, Ct)));

        var done = await AgentHarness.CollectAsync(chat.RespondToApprovalAsync(card.RequestId, true, Ct));
        var completed = done.OfType<ActionCompleted>().Single();
        Assert.StartsWith("TK-", completed.Reference, StringComparison.Ordinal);

        var card2 = (await AgentHarness.CollectAsync(chat.SendAsync("please make sure the ticket exists", Ct))).OfType<ApprovalCard>().Single();
        var again = await AgentHarness.CollectAsync(chat.RespondToApprovalAsync(card2.RequestId, true, Ct));
        Assert.Contains("already exists", again.OfType<AssistantMessage>().Single().Text, StringComparison.Ordinal);

        var tickets = await DbAsync(db => db.Tickets.AsNoTracking().Where(t => t.ConversationId == chat.Session.ConversationId).ToListAsync(Ct));
        var ticket = Assert.Single(tickets);
        Assert.Equal(completed.Reference, ticket.Number);
        Assert.Equal("BL-10482", ticket.OrderNumber);
        Assert.Equal("m***@example.com", ticket.CustomerLabel);
        Assert.DoesNotContain("maya.t@example.com", ticket.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conversation_transcript_tool_calls_approvals_and_tokens_are_persisted()
    {
        var model = new ScriptedChatClient(
            Invoke(SupportTools.GetOrderStatusName, Args(("orderNumber", "BL-10482"), ("email", FakeLegacyOrdersClient.MayaEmail))),
            Invoke(SupportTools.CreateReturnRequestName, Args(("orderNumber", "BL-10482"), ("sku", "AUD-AURORA-WH"), ("quantity", 1), ("condition", "defective"), ("reason", "left ear cuts out"))),
            Say("Return RR-2291 created."));
        var (chat, scope) = Create(model);
        await using var _ = scope;

        var card = (await AgentHarness.CollectAsync(chat.SendAsync("return my headphones BL-10482 maya.t@example.com", Ct))).OfType<ApprovalCard>().Single();
        await AgentHarness.CollectAsync(chat.RespondToApprovalAsync(card.RequestId, true, Ct));

        var id = chat.Session.ConversationId;
        var conversation = await DbAsync(db => db.Conversations.AsNoTracking().SingleAsync(c => c.Id == id, Ct));
        var messages = await DbAsync(db => db.Messages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Id).ToListAsync(Ct));
        var calls = await DbAsync(db => db.ToolCalls.AsNoTracking().Where(t => t.ConversationId == id).ToListAsync(Ct));

        Assert.Equal(FakeLegacyOrdersClient.MayaId, conversation.VerifiedCustomerId);
        Assert.Equal("m***@example.com", conversation.CustomerLabel);
        Assert.True(conversation.TokensIn > 0);
        Assert.Equal(MessageRole.User, messages[0].Role);
        Assert.Equal("Return RR-2291 created.", messages[^1].Text);
        var verify = calls.Single(c => c.Tool == SupportTools.GetOrderStatusName);
        Assert.Contains("m***@example.com", verify.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("maya.t@example.com", verify.Args, StringComparison.Ordinal);
        var write = calls.Single(c => c.Tool == SupportTools.CreateReturnRequestName);
        Assert.Equal(ApprovalState.Approved, write.Approval);
        Assert.Equal("ok", write.ResultStatus);
        Assert.NotNull(write.IdempotencyKey);
    }

    [Fact]
    public async Task A_repeat_ticket_with_a_reworded_subject_is_still_created_once()
    {
        // Found by the live eval (S19): asked to "create that same ticket again", the model added "(Duplicate request)".
        Dictionary<string, object?> Ticket(string subject) => Args(("subject", subject), ("category", "orders"), ("priority", "normal"),
            ("summary", "Customer needs a copy of the invoice."), ("orderNumber", "BL-10482"));
        var model = new ScriptedChatClient(
            Invoke(SupportTools.GetOrderStatusName, Args(("orderNumber", "BL-10482"), ("email", FakeLegacyOrdersClient.MayaEmail))),
            Invoke(SupportTools.CreateSupportTicketName, Ticket("Copy of invoice for order BL-10482")),
            messages => Text(LastToolResult(messages) ?? string.Empty),
            Invoke(SupportTools.CreateSupportTicketName, Ticket("Copy of invoice for order BL-10482 (Duplicate request)")),
            messages => Text(LastToolResult(messages) ?? string.Empty));
        var (chat, scope) = Create(model);
        await using var _ = scope;

        var card = (await AgentHarness.CollectAsync(chat.SendAsync("I need a copy of my invoice", Ct))).OfType<ApprovalCard>().Single();
        await AgentHarness.CollectAsync(chat.RespondToApprovalAsync(card.RequestId, true, Ct));
        var card2 = (await AgentHarness.CollectAsync(chat.SendAsync("create that same ticket again to be safe", Ct))).OfType<ApprovalCard>().Single();
        var again = await AgentHarness.CollectAsync(chat.RespondToApprovalAsync(card2.RequestId, true, Ct));

        Assert.Contains("already exists", again.OfType<AssistantMessage>().Single().Text, StringComparison.Ordinal);
        Assert.Equal(1, await DbAsync(db => db.Tickets.CountAsync(t => t.ConversationId == chat.Session.ConversationId, Ct)));
    }

    [Fact]
    public async Task Unrelated_tickets_without_an_order_are_not_merged()
    {
        Dictionary<string, object?> Ticket(string subject) => Args(("subject", subject), ("category", "other"), ("priority", "low"), ("summary", "Customer question for the team."));
        var model = new ScriptedChatClient(
            Invoke(SupportTools.CreateSupportTicketName, Ticket("Question about gift wrapping")),
            messages => Text(LastToolResult(messages) ?? string.Empty),
            Invoke(SupportTools.CreateSupportTicketName, Ticket("Question about corporate discounts")),
            messages => Text(LastToolResult(messages) ?? string.Empty));
        var (chat, scope) = Create(model);
        await using var _ = scope;

        var card = (await AgentHarness.CollectAsync(chat.SendAsync("ticket about gift wrap", Ct))).OfType<ApprovalCard>().Single();
        await AgentHarness.CollectAsync(chat.RespondToApprovalAsync(card.RequestId, true, Ct));
        var card2 = (await AgentHarness.CollectAsync(chat.SendAsync("and one about discounts", Ct))).OfType<ApprovalCard>().Single();
        await AgentHarness.CollectAsync(chat.RespondToApprovalAsync(card2.RequestId, true, Ct));

        Assert.Equal(2, await DbAsync(db => db.Tickets.CountAsync(t => t.ConversationId == chat.Session.ConversationId, Ct)));
    }

    [Fact]
    public async Task Escalation_creates_one_high_priority_ticket_and_marks_the_conversation()
    {
        var model = new ScriptedChatClient(
            Invoke(SupportTools.EscalateToHumanName, Args(("reason", "Customer wants a person about a burning smell from the charger."))),
            messages => Text(LastToolResult(messages) ?? string.Empty),
            Invoke(SupportTools.EscalateToHumanName, Args(("reason", "again"))),
            Say("Already escalated."));
        var (chat, scope) = Create(model);
        await using var _ = scope;

        var events = await AgentHarness.CollectAsync(chat.SendAsync("I want a human, my charger smells of burning", Ct));
        await AgentHarness.CollectAsync(chat.SendAsync("hello??", Ct));

        Assert.Contains("4 business hours", events.OfType<AssistantMessage>().Single().Text, StringComparison.Ordinal);
        var id = chat.Session.ConversationId;
        var tickets = await DbAsync(db => db.Tickets.AsNoTracking().Where(t => t.ConversationId == id).ToListAsync(Ct));
        Assert.Equal(TicketPriority.High, Assert.Single(tickets).Priority);
        Assert.Equal(ConversationStatus.Escalated, (await DbAsync(db => db.Conversations.AsNoTracking().SingleAsync(c => c.Id == id, Ct))).Status);
    }

    [Fact]
    public async Task Ticket_numbers_follow_the_demo_sequence()
    {
        var number = await DbAsync(db => db.Database.SqlQueryRaw<string>("SELECT 'TK-' || last_value::text AS \"Value\" FROM ticket_number_seq").SingleAsync(Ct));
        Assert.StartsWith("TK-5", number, StringComparison.Ordinal);
    }
}
