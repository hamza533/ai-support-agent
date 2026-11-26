using System.Net;
using Brightline.Agent;
using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.IntegrationTests.Infrastructure;
using Brightline.LegacyOrders.Client;
using Brightline.Testing;
using Brightline.Web.Admin;
using Brightline.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Brightline.Testing.ScriptedChatClient;

namespace Brightline.IntegrationTests.App;

public sealed class AdminTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private const string StaffEmail = "staff@brightline.test";
    private const string StaffPassword = "Staff-Password-123!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task EnsureStaffAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>().CreateUserAsync(StaffEmail, Roles.Staff, StaffPassword);
    }

    [Theory]
    [InlineData("/admin", "Overview")]
    [InlineData("/admin/conversations", "Conversations")]
    [InlineData("/admin/tickets", "Tickets")]
    [InlineData("/admin/documents", "Documents")]
    [InlineData("/admin/settings", "Settings")]
    [InlineData("/admin/test-chat", "Test as customer")]
    public async Task Staff_can_open_every_admin_page(string path, string heading)
    {
        await EnsureStaffAsync();
        using var client = await fixture.Factory.CreateSignedInClientAsync(StaffEmail, StaffPassword);

        var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(heading, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/admin/conversations")]
    [InlineData("/admin/settings")]
    [InlineData("/admin/documents")]
    public async Task Anonymous_users_are_sent_to_sign_in(string path)
    {
        using var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_are_read_only_for_staff_and_editable_for_admins()
    {
        await EnsureStaffAsync();
        using var staff = await fixture.Factory.CreateSignedInClientAsync(StaffEmail, StaffPassword);
        using var admin = await fixture.Factory.CreateSignedInClientAsync();

        var staffPage = await staff.GetStringAsync("/admin/settings", Ct);
        var adminPage = await admin.GetStringAsync("/admin/settings", Ct);

        Assert.Contains("Read-only (admin role required)", staffPage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"settings-save\"", staffPage, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"settings-save\"", adminPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_and_conversation_detail_reflect_agent_activity()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var model = new ScriptedChatClient(
            Invoke(SupportTools.EscalateToHumanName, new Dictionary<string, object?> { ["reason"] = "Customer asked for a person." }),
            Say("Escalated."));
        var chat = ActivatorUtilities.CreateInstance<ChatService>(scope.ServiceProvider, (IChatClient)model, (ILegacyOrdersClient)new FakeLegacyOrdersClient());
        await AgentHarness.CollectAsync(chat.SendAsync("I want a human", Ct));

        var admin = scope.ServiceProvider.GetRequiredService<AdminService>();
        var stats = await admin.GetOverviewAsync(Ct);
        var detail = await admin.GetConversationAsync(chat.Session.ConversationId, Ct);
        var escalated = await admin.ListConversationsAsync(ConversationFilter.Escalated, null, Ct);

        Assert.True(stats.ConversationsToday >= 1);
        Assert.True(stats.TicketsToday >= 1);
        Assert.Contains(stats.NeedsAttention, t => t.ConversationId == chat.Session.ConversationId);
        Assert.Contains(stats.RecentActions, a => a.Action == "Escalated to a person");
        Assert.Equal(14, stats.PerDay.Count);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Messages.Count);
        Assert.Single(detail.ToolCalls);
        Assert.True(detail.EstimatedCost > 0);
        Assert.Contains(escalated, c => c.Id == chat.Session.ConversationId);
    }

    [Fact]
    public async Task Staff_can_change_ticket_status_and_notes()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = new Ticket
        {
            Category = "warranty",
            Subject = "Test ticket",
            Summary = "Summary",
            IdempotencyKey = $"test-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(Ct);

        var admin = scope.ServiceProvider.GetRequiredService<AdminService>();
        Assert.True(await admin.UpdateTicketAsync(ticket.Id, TicketStatus.InProgress, "Called the carrier.", Ct));

        var saved = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id, Ct);
        Assert.Equal(TicketStatus.InProgress, saved.Status);
        Assert.Equal("Called the carrier.", saved.InternalNotes);
        Assert.StartsWith("TK-", saved.Number, StringComparison.Ordinal);
    }
}
