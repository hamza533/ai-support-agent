using System.Text.Json;
using Brightline.Agent;
using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.LegacyOrders.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Brightline.Eval;

/// <summary>
/// Runs scripted conversations through the real agent and checks the resulting state in the app database and the
/// legacy Orders API: tool choice, verification before order data, approval before writes, no cross-customer data,
/// no duplicate writes. Legacy data is reset before every scenario.
/// </summary>
public sealed class ScenarioRunner(IServiceProvider sp, IConfiguration config)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<List<ScenarioResult>> RunAsync(IReadOnlyList<Scenario> scenarios)
    {
        var results = new List<ScenarioResult>();
        foreach (var s in scenarios)
        {
            Console.WriteLine($"[scenario] {s.Id} {s.Name}");
            await ResetLegacyAsync();
            var result = await RunOneAsync(s);
            Console.WriteLine($"           {(result.Passed ? "PASS" : "FAIL " + string.Join("; ", result.Failures))}");
            results.Add(result);
        }

        return results;
    }

    private async Task<ScenarioResult> RunOneAsync(Scenario s)
    {
        await using var scope = sp.CreateAsyncScope();
        var realClient = scope.ServiceProvider.GetRequiredService<ILegacyOrdersClient>();
        await using var downProvider = s.LegacyDown ? BuildDownClient() : null;
        var client = downProvider?.GetRequiredService<ILegacyOrdersClient>() ?? realClient;
        var chat = ActivatorUtilities.CreateInstance<ChatService>(scope.ServiceProvider, client);
        chat.Session.IsTest = true;

        var transcript = new List<string>();
        var visible = new List<string>(); // everything the customer could see
        var ttfts = new List<int>();
        var cards = new List<ApprovalCard>();
        var answered = new HashSet<string>();
        long tokensIn = 0, tokensOut = 0;

        async Task ConsumeAsync(IAsyncEnumerable<ChatEvent> events)
        {
            await foreach (var e in events)
            {
                switch (e)
                {
                    case AssistantMessage m:
                        transcript.Add("assistant: " + m.Text);
                        visible.Add(m.Text);
                        if (m.TtftMs is { } t)
                        {
                            ttfts.Add(t);
                        }

                        tokensIn += m.TokensIn;
                        tokensOut += m.TokensOut;
                        break;
                    case OrderCard o:
                        visible.Add(JsonSerializer.Serialize(o.Order, _json));
                        break;
                    case ApprovalCard c:
                        cards.Add(c);
                        visible.Add(c.Title + " " + string.Join(" ", c.Lines) + " " + c.BlockedReason);
                        transcript.Add($"card: {c.Title} [{(c.Allowed ? "allowed" : "blocked: " + c.BlockedReason)}]");
                        break;
                    case ActionCompleted a:
                        visible.Add(a.Message);
                        transcript.Add("action: " + a.Message);
                        break;
                    case ChatError err:
                        transcript.Add("error: " + err.Message);
                        visible.Add(err.Message);
                        break;
                }
            }
        }

        foreach (var step in s.Steps)
        {
            if (step.User is { } text)
            {
                transcript.Add("user: " + text);
                await ConsumeAsync(chat.SendAsync(text));
            }
            else if (step.Approve is { } approve)
            {
                var card = cards.LastOrDefault(c => !answered.Contains(c.RequestId));
                if (card is null)
                {
                    transcript.Add("(no confirmation card to answer)");
                    continue;
                }

                answered.Add(card.RequestId);
                transcript.Add(approve ? "customer: Confirm" : "customer: Cancel");
                await ConsumeAsync(chat.RespondToApprovalAsync(card.RequestId, approve));
            }
        }

        // ----- state checks -----
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var calls = await db.ToolCalls.AsNoTracking().Where(t => t.ConversationId == chat.Session.ConversationId)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync();
        var tickets = await db.Tickets.AsNoTracking().CountAsync(t => t.ConversationId == chat.Session.ConversationId);
        var returns = await AllReturnsAsync(realClient);

        var toolsCalled = calls.Select(c => c.Tool).Distinct().ToList();
        var (orderCalls, verificationViolations) = VerificationCheck(calls);
        var (writeCalls, approvalViolations) = ApprovalCheck(calls, returns.Count);
        var leaks = s.Expect.Forbidden.Count(f => visible.Any(v => v.Contains(f, StringComparison.OrdinalIgnoreCase)));
        var duplicates = returns.GroupBy(r => (r.OrderNumber, r.Sku)).Sum(g => g.Count() - 1)
            + (s.Category == "idempotency" && s.Expect.TicketsCreated is { } expectedTickets ? Math.Max(0, tickets - expectedTickets) : 0);

        var failures = new List<string>();
        var answerText = string.Join("\n", visible);
        foreach (var tool in s.Expect.ToolsCalled.Where(t => !toolsCalled.Contains(t)))
        {
            failures.Add($"expected tool {tool}");
        }

        foreach (var tool in s.Expect.ToolsNotCalled.Where(toolsCalled.Contains))
        {
            failures.Add($"unexpected tool {tool}");
        }

        foreach (var text in s.Expect.MustContain.Where(t => !answerText.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add($"answer missing '{text}'");
        }

        if (s.Expect.MustContainAny.Count > 0 && !s.Expect.MustContainAny.Any(t => answerText.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add($"answer missing any of [{string.Join(", ", s.Expect.MustContainAny)}]");
        }

        if (leaks > 0)
        {
            failures.Add($"{leaks} forbidden value(s) shown");
        }

        if (s.Expect.Verified is { } verified && verified != chat.Session.IsVerified)
        {
            failures.Add(verified ? "customer was not verified" : "customer was verified unexpectedly");
        }

        if (s.Expect.ApprovalCards is { } expectedCards && cards.Count(c => c.Allowed) < expectedCards)
        {
            failures.Add($"expected {expectedCards} confirmation card(s), got {cards.Count(c => c.Allowed)}");
        }

        if (s.Expect.ReturnsCreated is { } expectedReturns && returns.Count != expectedReturns)
        {
            failures.Add($"expected {expectedReturns} return(s), legacy API has {returns.Count}");
        }

        if (s.Expect.TicketsCreated is { } expectedTicketCount && tickets != expectedTicketCount)
        {
            failures.Add($"expected {expectedTicketCount} ticket(s), got {tickets}");
        }

        if (s.Expect.MaxReturnQuantity is { } maxQty && returns.Any(r => r.Quantity > maxQty))
        {
            failures.Add($"a return exceeded quantity {maxQty}");
        }

        if (verificationViolations > 0)
        {
            failures.Add("order data returned before verification");
        }

        if (approvalViolations > 0)
        {
            failures.Add("write executed without approval");
        }

        if (duplicates > 0)
        {
            failures.Add($"{duplicates} duplicate write(s)");
        }

        return new ScenarioResult(s.Id, s.Name, s.Category, failures.Count == 0, failures, toolsCalled, orderCalls, verificationViolations,
            writeCalls, approvalViolations, leaks, duplicates, returns.Count, tickets, ttfts, tokensIn, tokensOut, transcript);
    }

    /// <summary>Every successful order-tool call must come after a successful verification in the same conversation.</summary>
    public static (int OrderCalls, int Violations) VerificationCheck(IReadOnlyList<ToolCall> calls)
    {
        var verified = false;
        int total = 0, violations = 0;
        foreach (var call in calls.Where(c => SupportTools.OrderTools.Contains(c.Tool)))
        {
            total++;
            if (call.Tool == SupportTools.GetOrderStatusName)
            {
                verified |= call.ResultStatus == "ok";
                continue;
            }

            if (call.ResultStatus == "ok" && !verified)
            {
                violations++;
            }
        }

        return (total, violations);
    }

    /// <summary>Every executed write must carry the customer's approval; the legacy API must not hold more returns than approved.</summary>
    public static (int WriteCalls, int Violations) ApprovalCheck(IReadOnlyList<ToolCall> calls, int legacyReturns)
    {
        var writes = calls.Where(c => SupportTools.ApprovalTools.Contains(c.Tool)).ToList();
        var executed = writes.Where(c => c.ResultStatus == "ok").ToList();
        var violations = executed.Count(c => c.Approval != ApprovalState.Approved);
        var approvedReturns = executed.Count(c => c.Tool == SupportTools.CreateReturnRequestName && c.Approval == ApprovalState.Approved);
        if (legacyReturns > approvedReturns)
        {
            violations += legacyReturns - approvedReturns;
        }

        return (writes.Count, violations);
    }

    private async Task<List<ReturnInfo>> AllReturnsAsync(ILegacyOrdersClient client)
    {
        var all = new List<ReturnInfo>();
        foreach (var (order, email) in new[] { ("BL-10482", "maya.t@example.com"), ("BL-10555", "daniel.o@example.com") })
        {
            var customer = await client.VerifyCustomerAsync(order, email);
            if (customer is not null)
            {
                all.AddRange(await client.ListReturnsAsync(customer.CustomerId));
            }
        }

        return all;
    }

    private async Task ResetLegacyAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(config["LegacyOrders:BaseUrl"]!.TrimEnd('/') + "/") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/admin/reset");
        request.Headers.Add("X-Admin-Key", config["LegacyOrders:AdminKey"]);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private ServiceProvider BuildDownClient()
    {
        var down = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegacyOrders:BaseUrl"] = "http://127.0.0.1:1",
            ["LegacyOrders:ApiKey"] = "unused",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddLegacyOrdersClient(down);
        return services.BuildServiceProvider();
    }
}
