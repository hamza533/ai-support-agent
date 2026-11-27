using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.Knowledge.Search;
using Brightline.LegacyOrders.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Brightline.Agent;

/// <summary>
/// The agent's seven tools. One instance per conversation, bound to that conversation's <see cref="ConversationSession"/>.
/// No tool takes a customer id: the verified id is read from the session, so a forged id in the model's arguments has
/// nothing to bind to.
/// </summary>
public sealed partial class SupportTools(
    ConversationSession session,
    ILegacyOrdersClient orders,
    IKnowledgeSearch search,
    AgentSettings settings,
    IDbContextFactory<AppDbContext> dbFactory,
    IConversationStore store,
    TimeProvider time,
    ILogger<SupportTools> logger)
{
    public const string SearchKnowledgeBaseName = "search_knowledge_base";
    public const string GetOrderStatusName = "get_order_status";
    public const string ListMyOrdersName = "list_my_orders";
    public const string CheckReturnEligibilityName = "check_return_eligibility";
    public const string CreateReturnRequestName = "create_return_request";
    public const string CreateSupportTicketName = "create_support_ticket";
    public const string EscalateToHumanName = "escalate_to_human";

    public static readonly string[] WriteTools = [CreateReturnRequestName, CreateSupportTicketName, EscalateToHumanName];
    public static readonly string[] ApprovalTools = [CreateReturnRequestName, CreateSupportTicketName];
    public static readonly string[] OrderTools = [GetOrderStatusName, ListMyOrdersName, CheckReturnEligibilityName, CreateReturnRequestName];

    // Tool results go to the model, never into HTML: keep <untrusted_data> tags readable instead of <-escaped.
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The tools as AI functions; the two data-changing tools require the customer's approval.</summary>
    public IList<AITool> AsAITools()
    {
        AIFunction Make(Delegate method, string name) =>
            AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name });

        return
        [
            Make(SearchKnowledgeBaseAsync, SearchKnowledgeBaseName),
            Make(GetOrderStatusAsync, GetOrderStatusName),
            Make(ListMyOrdersAsync, ListMyOrdersName),
            Make(CheckReturnEligibilityAsync, CheckReturnEligibilityName),
            new ApprovalRequiredAIFunction(Make(CreateReturnRequestAsync, CreateReturnRequestName)),
            new ApprovalRequiredAIFunction(Make(CreateSupportTicketAsync, CreateSupportTicketName)),
            Make(EscalateToHumanAsync, EscalateToHumanName),
        ];
    }

    [Description("Search Brightline's help-centre articles (returns, refunds, delivery, warranty, payments, troubleshooting). Always use this for policy or how-to questions. Results are numbered sources to cite as [n].")]
    public Task<string> SearchKnowledgeBaseAsync(
        [Description("A short search query in plain English, e.g. 'return headphones defective'.")] string query,
        CancellationToken ct = default) =>
        RunAsync(SearchKnowledgeBaseName, new { query }, null, async () =>
        {
            query = (query ?? string.Empty).Trim();
            if (query.Length is < 2 or > 300)
            {
                return Fail("invalid_query", "Search query must be 2-300 characters.");
            }

            var hits = await search.SearchAsync(query, ct: ct);
            var citations = session.RegisterSources(hits);
            if (hits.Count == 0)
            {
                return Ok(new { results = Array.Empty<object>(), note = "No help-centre article matched. If you can't answer, say \"I don't know\" and offer a support ticket." }, "0 results");
            }

            var results = hits.Zip(citations, (h, c) => new
            {
                source = c.Number,
                content = UntrustedData.Wrap("help-centre", $"Article: {h.Title}\nSection: {h.HeadingPath}\n\n{h.Text}"),
            });
            return Ok(new { results }, $"{hits.Count} results");
        }, ct);

    [Description("Verify the customer and get an order's status. Requires BOTH the order number and the email address on that order. Returns the order's items, status and delivery.")]
    public Task<string> GetOrderStatusAsync(
        [Description("Order number, format BL-12345.")] string orderNumber,
        [Description("The email address the customer used on that order.")] string email,
        CancellationToken ct = default) =>
        RunAsync(GetOrderStatusName, new { orderNumber, email }, null, async () =>
        {
            orderNumber = NormalizeOrderNumber(orderNumber);
            if (!OrderNumberRegex().IsMatch(orderNumber))
            {
                return Fail("invalid_order_number", "Order numbers look like BL-12345.");
            }

            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
            {
                return Fail("email_required", "Ask the customer for the email address used on the order.");
            }

            if (session.VerificationLocked)
            {
                return Fail("verification_locked", "Too many failed verification attempts in this chat. Offer to create a support ticket.");
            }

            var customer = await orders.VerifyCustomerAsync(orderNumber, email.Trim(), ct);
            if (customer is null)
            {
                session.RecordFailedVerification();
                return Fail("verification_failed", "The order number and email don't match. Ask the customer to check both. Do not reveal anything about the order.");
            }

            if (!session.TryVerify(customer, email))
            {
                // Same answer as a mismatch (no confirmation that another customer's details are valid) and it counts as a failure.
                session.RecordFailedVerification();
                return Fail("verification_failed", "These details can't be used in this chat (it is already verified for one customer). Ask the customer to start a new chat for a different order.");
            }

            await store.MarkVerifiedAsync(session, ct);
            session.Publish(new CustomerVerified(customer.FirstName));
            var order = await orders.GetOrderAsync(customer.CustomerId, orderNumber, ct);
            if (order is null)
            {
                return Fail("not_found", "Order not found.");
            }

            session.Publish(new OrderCard(order));
            return Ok(new { verified = true, customerFirstName = customer.FirstName, order = UntrustedData.Wrap("order-system", JsonSerializer.Serialize(OrderView(order), _json)) }, $"verified; {order.Status}");
        }, ct);

    [Description("List the verified customer's orders (newest first). Only works after get_order_status has verified the customer in this chat.")]
    public Task<string> ListMyOrdersAsync(CancellationToken ct = default) =>
        RunAsync(ListMyOrdersName, new { }, null, async () =>
        {
            if (session.VerifiedCustomerId is not { } customerId)
            {
                return NotVerified();
            }

            var list = await orders.ListOrdersAsync(customerId, ct);
            return Ok(new { orders = UntrustedData.Wrap("order-system", JsonSerializer.Serialize(list.Take(20), _json)) }, $"{list.Count} orders");
        }, ct);

    [Description("Check whether an item on the verified customer's order can be returned, and why (days since delivery, category, condition).")]
    public Task<string> CheckReturnEligibilityAsync(
        [Description("Order number, format BL-12345.")] string orderNumber,
        [Description("The item's SKU from the order, e.g. AUD-AURORA-WH.")] string sku,
        [Description("Item condition: \"defective\", \"unopened\" or \"opened_working\".")] string condition,
        CancellationToken ct = default) =>
        RunAsync(CheckReturnEligibilityName, new { orderNumber, sku, condition }, null, async () =>
        {
            if (session.VerifiedCustomerId is not { } customerId)
            {
                return NotVerified();
            }

            var (order, line, error) = await LoadLineAsync(customerId, orderNumber, sku, ct);
            if (error is not null)
            {
                return error;
            }

            var result = ReturnPolicy.Evaluate(order!, line!, ReturnPolicy.ParseCondition(condition), settings, time.GetUtcNow());
            return Ok(new
            {
                eligible = result.Eligible,
                reasons = result.Reasons,
                daysSinceDelivery = result.DaysSinceDelivery,
                windowDays = result.WindowDays,
                category = result.Category,
                item = UntrustedData.Neutralize(line!.Name),
                returnableQuantity = result.ReturnableQuantity,
                maxItemsPerReturn = settings.MaxItemsPerReturn,
                refund = "Refund to the original payment method once the item is received and inspected.",
            }, result.Summary);
        }, ct);

    [Description("Create a return request for an item on the verified customer's order. Requires the customer's confirmation (the system shows a confirmation card). Call check_return_eligibility first.")]
    public Task<string> CreateReturnRequestAsync(
        [Description("Order number, format BL-12345.")] string orderNumber,
        [Description("The item's SKU from the order.")] string sku,
        [Description("How many units to return.")] int quantity,
        [Description("Item condition: \"defective\", \"unopened\" or \"opened_working\".")] string condition,
        [Description("Short reason in the customer's words, e.g. 'left ear cuts out'.")] string reason,
        CancellationToken ct = default)
    {
        sku ??= string.Empty;
        string? key = null;
        return RunAsync(CreateReturnRequestName, new { orderNumber, sku, quantity, condition, reason }, () => key, async () =>
        {
            var check = await PrepareReturnAsync(orderNumber, sku, quantity, condition, reason, ct);
            if (check.Error is not null)
            {
                return check.Error;
            }

            // Keyed on conversation + resolved order line (not the model's wording of the SKU), so a retry that names
            // the item differently still maps to the same return.
            key = IdempotencyKey(session.ConversationId, "return", check.Order!.Number, check.Line!.LineId.ToString(CultureInfo.InvariantCulture));

            var result = await orders.CreateReturnAsync(
                session.VerifiedCustomerId!.Value,
                new NewReturn(check.Order!.Number, check.Line!.Sku, quantity, $"{ConditionLabel(check.Condition)}: {reason}".Trim()),
                key,
                ct);
            var message = result.Created
                ? $"Return {result.Return.Number} created for {quantity} × {check.Line.Name}. A prepaid label will be emailed; refund to the original payment method after inspection."
                : $"Return {result.Return.Number} already exists for this item (no duplicate created).";
            session.Publish(new ActionCompleted("return", result.Return.Number, message));
            return Ok(new { returnNumber = result.Return.Number, created = result.Created, message }, $"{result.Return.Number} ({(result.Created ? "created" : "existing")})");
        }, ct, ApprovalState.Approved);
    }

    [Description("Create a support ticket for the team (e.g. a defect, a question the help centre can't answer, a cancellation or warranty claim). Requires the customer's confirmation.")]
    public Task<string> CreateSupportTicketAsync(
        [Description("Short subject, e.g. 'Aurora headphones: left ear cuts out'.")] string subject,
        [Description("One of: returns, delivery, warranty, payments, orders, account, other.")] string category,
        [Description("One of: low, normal, high.")] string priority,
        [Description("Summary of the conversation and the customer's request, 1-4 sentences.")] string summary,
        [Description("Related order number if any, format BL-12345, otherwise empty.")] string? orderNumber = null,
        CancellationToken ct = default)
    {
        var key = IdempotencyKey(session.ConversationId, "ticket", Normalize(subject), Normalize(category));
        return RunAsync(CreateSupportTicketName, new { subject, category, priority, summary, orderNumber }, key, async () =>
        {
            var prepared = await PrepareOwnedTicketAsync(subject, category, priority, summary, orderNumber, ct);
            if (prepared.Error is not null)
            {
                return prepared.Error;
            }

            var (ticket, created) = await CreateTicketAsync(prepared.Ticket!, key, ct);
            var message = $"Ticket {ticket.Number} {(created ? "created" : "already exists")}. Our team replies within 1 business day.";
            session.Publish(new ActionCompleted("ticket", ticket.Number, message));
            return Ok(new { ticketNumber = ticket.Number, created, expectedResponse = "within 1 business day", message }, $"{ticket.Number} ({(created ? "created" : "existing")})");
        }, ct, ApprovalState.Approved);
    }

    [Description("Hand the conversation to a human agent: creates a high-priority ticket with a summary. Use when the customer asks for a person, is frustrated, or reports a safety issue.")]
    public Task<string> EscalateToHumanAsync(
        [Description("Why the customer needs a person, plus a short summary of the conversation.")] string reason,
        CancellationToken ct = default)
    {
        var key = IdempotencyKey(session.ConversationId, "escalate");
        return RunAsync(EscalateToHumanName, new { reason }, key, async () =>
        {
            var ticket = new Ticket
            {
                ConversationId = session.ConversationId,
                Category = "escalation",
                Priority = TicketPriority.High,
                Subject = "Customer asked for a human",
                Summary = PiiMasker.MaskText(Truncate(reason, 2000)),
                CustomerLabel = session.VerifiedEmailMasked,
                IdempotencyKey = key,
            };
            var (saved, created) = await CreateTicketAsync(ticket, key, ct);
            await store.SetStatusAsync(session.ConversationId, ConversationStatus.Escalated, ct);
            var message = $"Escalated as ticket {saved.Number}. A member of our team will reply within 4 business hours.";
            session.Publish(new ActionCompleted("escalation", saved.Number, message));
            return Ok(new { ticketNumber = saved.Number, created, expectedResponse = "within 4 business hours", message }, $"{saved.Number} (high)");
        }, ct);
    }

    /// <summary>
    /// Builds the confirmation card for a pending write from the model's arguments – after validating them and
    /// re-checking the business rules on the server. A card that isn't allowed can only be cancelled.
    /// </summary>
    public async Task<ApprovalCard> DescribeApprovalAsync(string requestId, FunctionCallContent call, CancellationToken ct)
    {
        string Arg(string name) => call.Arguments?.TryGetValue(name, out var v) == true ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

        if (call.Name == CreateReturnRequestName)
        {
            _ = int.TryParse(Arg("quantity"), out var quantity);
            var check = await PrepareReturnAsync(Arg("orderNumber"), Arg("sku"), quantity, Arg("condition"), Arg("reason"), ct);
            if (check.Error is not null)
            {
                return new ApprovalCard(requestId, call.Name, "Return request can't be created", [], string.Empty, false, ErrorMessage(check.Error));
            }

            return new ApprovalCard(
                requestId,
                call.Name,
                "Confirm return request",
                [
                    $"Return {quantity} × {check.Line!.Name} from order {check.Order!.Number}",
                    $"Reason: {ConditionLabel(check.Condition).ToLowerInvariant()} ({Truncate(Arg("reason"), 120)})",
                    "Refund to original payment method per policy",
                ],
                "Nothing changes until you confirm.",
                true,
                null);
        }

        if (call.Name == CreateSupportTicketName)
        {
            var prepared = await PrepareOwnedTicketAsync(Arg("subject"), Arg("category"), Arg("priority"), Arg("summary"), Arg("orderNumber"), ct);
            if (prepared.Error is not null)
            {
                return new ApprovalCard(requestId, call.Name, "Ticket can't be created", [], string.Empty, false, ErrorMessage(prepared.Error));
            }

            var t = prepared.Ticket!;
            var lines = new List<string> { $"Open a support ticket: \"{t.Subject}\"", $"Category: {t.Category} · Priority: {t.Priority.ToString().ToLowerInvariant()}" };
            if (t.OrderNumber is not null)
            {
                lines.Add($"Linked order: {t.OrderNumber}");
            }

            lines.Add("Our team replies within 1 business day");
            return new ApprovalCard(requestId, call.Name, "Confirm support ticket", lines, "Nothing changes until you confirm.", true, null);
        }

        return new ApprovalCard(requestId, call.Name, "Unknown action", [], string.Empty, false, "This action isn't supported.");
    }

    private sealed record ReturnCheck(OrderDetail? Order, OrderLine? Line, ItemCondition Condition, string? Error);

    /// <summary>Server-side re-check of every business rule for a return (verification, ownership, window, category, quantity).</summary>
    private async Task<ReturnCheck> PrepareReturnAsync(string orderNumber, string sku, int quantity, string condition, string reason, CancellationToken ct)
    {
        var parsedCondition = ReturnPolicy.ParseCondition(condition);
        if (session.VerifiedCustomerId is not { } customerId)
        {
            return new ReturnCheck(null, null, parsedCondition, NotVerified());
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            return new ReturnCheck(null, null, parsedCondition, Fail("reason_required", "Ask the customer for a short reason (max 500 characters)."));
        }

        var (order, line, error) = await LoadLineAsync(customerId, orderNumber, sku, ct);
        if (error is not null)
        {
            return new ReturnCheck(null, null, parsedCondition, error);
        }

        var eligibility = ReturnPolicy.Evaluate(order!, line!, parsedCondition, settings, time.GetUtcNow());
        if (!eligibility.Eligible)
        {
            return new ReturnCheck(order, line, parsedCondition, Fail("not_eligible", eligibility.Summary));
        }

        var maxQuantity = Math.Min(line!.ReturnableQuantity, settings.MaxItemsPerReturn);
        if (quantity < 1 || quantity > maxQuantity)
        {
            return new ReturnCheck(order, line, parsedCondition, Fail("invalid_quantity", $"Quantity must be between 1 and {maxQuantity}."));
        }

        return new ReturnCheck(order, line, parsedCondition, null);
    }

    private sealed record TicketCheck(Ticket? Ticket, string? Error);

    /// <summary>Like <see cref="PrepareTicket"/>, and only keeps the order link if the order belongs to the verified customer.</summary>
    private async Task<TicketCheck> PrepareOwnedTicketAsync(string subject, string category, string priority, string summary, string? orderNumber, CancellationToken ct)
    {
        var check = PrepareTicket(subject, category, priority, summary, orderNumber);
        if (check.Ticket?.OrderNumber is { } linked && session.VerifiedCustomerId is { } customerId)
        {
            try
            {
                if (await orders.GetOrderAsync(customerId, linked, ct) is null)
                {
                    check.Ticket.OrderNumber = null;
                }
            }
            catch (LegacyOrdersUnavailableException)
            {
                check.Ticket.OrderNumber = null; // can't prove ownership right now: don't link
            }
        }

        return check;
    }

    private TicketCheck PrepareTicket(string subject, string category, string priority, string summary, string? orderNumber)
    {
        subject = (subject ?? string.Empty).Trim();
        summary = (summary ?? string.Empty).Trim();
        if (subject.Length is < 3 or > 200)
        {
            return new TicketCheck(null, Fail("invalid_subject", "Subject must be 3-200 characters."));
        }

        if (summary.Length is < 5 or > 2000)
        {
            return new TicketCheck(null, Fail("invalid_summary", "Summary must be 5-2000 characters."));
        }

        var normalizedCategory = Normalize(category) switch
        {
            "returns" or "delivery" or "warranty" or "payments" or "orders" or "account" => Normalize(category),
            _ => "other",
        };
        var parsedPriority = Normalize(priority) switch
        {
            "low" => TicketPriority.Low,
            "high" or "urgent" => TicketPriority.High,
            _ => TicketPriority.Normal,
        };

        // An order number is only linked when this chat is verified (never leak or attach someone else's order).
        var linkedOrder = session.IsVerified && !string.IsNullOrWhiteSpace(orderNumber) && OrderNumberRegex().IsMatch(NormalizeOrderNumber(orderNumber))
            ? NormalizeOrderNumber(orderNumber)
            : null;

        return new TicketCheck(new Ticket
        {
            ConversationId = session.ConversationId,
            Category = normalizedCategory,
            Priority = parsedPriority,
            Subject = PiiMasker.MaskText(subject),
            Summary = PiiMasker.MaskText(summary),
            CustomerLabel = session.VerifiedEmailMasked,
            OrderNumber = linkedOrder,
            IdempotencyKey = string.Empty,
        }, null);
    }

    private async Task<(Ticket Ticket, bool Created)> CreateTicketAsync(Ticket ticket, string key, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            return (existing, false);
        }

        var now = time.GetUtcNow();
        ticket.IdempotencyKey = key;
        ticket.ConversationId = session.ConversationId;
        ticket.CreatedAt = now;
        ticket.UpdatedAt = now;
        await store.EnsurePersistedAsync(session, ct);
        db.Tickets.Add(ticket);
        try
        {
            await db.SaveChangesAsync(ct);
            return (await db.Tickets.AsNoTracking().SingleAsync(t => t.IdempotencyKey == key, ct), true);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await using var retry = await dbFactory.CreateDbContextAsync(ct);
            return (await retry.Tickets.AsNoTracking().SingleAsync(t => t.IdempotencyKey == key, ct), false);
        }
    }

    private async Task<(OrderDetail? Order, OrderLine? Line, string? Error)> LoadLineAsync(int customerId, string orderNumber, string sku, CancellationToken ct)
    {
        orderNumber = NormalizeOrderNumber(orderNumber);
        if (!OrderNumberRegex().IsMatch(orderNumber))
        {
            return (null, null, Fail("invalid_order_number", "Order numbers look like BL-12345."));
        }

        // The legacy API only returns orders of the customer id we pass – and we only ever pass the verified one.
        var order = await orders.GetOrderAsync(customerId, orderNumber, ct);
        if (order is null)
        {
            return (null, null, Fail("not_found", "No order with that number belongs to the verified customer."));
        }

        var line = order.Lines.FirstOrDefault(l => string.Equals(l.Sku, sku?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? order.Lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(sku) && l.Name.Contains(sku.Trim(), StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return (order, null, Fail("item_not_found", $"Item not on order {order.Number}. Items: {string.Join(", ", order.Lines.Select(l => $"{l.Sku} ({l.Name})"))}."));
        }

        return (order, line, null);
    }

    /// <summary>Runs a tool: timing, error mapping (order system down → offer a ticket) and the masked audit record.</summary>
    private Task<string> RunAsync(string tool, object args, string? idempotencyKey, Func<Task<string>> body, CancellationToken ct, ApprovalState approval = ApprovalState.NotRequired) =>
        RunAsync(tool, args, () => idempotencyKey, body, ct, approval);

    private async Task<string> RunAsync(string tool, object args, Func<string?> idempotencyKey, Func<Task<string>> body, CancellationToken ct, ApprovalState approval)
    {
        var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? Guid.NewGuid().ToString("N");
        session.Publish(new ToolActivity(callId, tool, ToolLabel(tool), "running"));
        var sw = Stopwatch.StartNew();
        string result;
        string status;
        try
        {
            result = await body();
            status = result.StartsWith("{\"error\"", StringComparison.Ordinal) ? "error" : "ok";
        }
        catch (LegacyOrdersUnavailableException ex)
        {
            logger.LogWarning("Tool {Tool}: order system unavailable ({Message})", tool, ex.Message);
            result = Fail("order_system_unavailable", "The order system can't be reached right now. Tell the customer and offer to create a support ticket instead.");
            status = "unavailable";
        }
        catch (LegacyOrdersRejectedException ex)
        {
            result = Fail("rejected_by_order_system", ex.Message);
            status = "rejected";
        }

        sw.Stop();
        var summary = ExtractSummary(ref result);
        session.Publish(new ToolActivity(callId, tool, ToolLabel(tool), status == "ok" ? "succeeded" : "failed"));
        await store.RecordToolCallAsync(session, new ToolCallRecord(callId, tool, MaskArgs(args), status, summary, approval, idempotencyKey(), (int)sw.ElapsedMilliseconds), ct);
        return result;
    }

    public static string ToolLabel(string tool) => tool switch
    {
        SearchKnowledgeBaseName => "Searched help articles",
        GetOrderStatusName => "Verified order",
        ListMyOrdersName => "Listed orders",
        CheckReturnEligibilityName => "Checked eligibility",
        CreateReturnRequestName => "Created return request",
        CreateSupportTicketName => "Created support ticket",
        EscalateToHumanName => "Escalated to a person",
        _ => tool,
    };

    internal static string MaskArgs(object args)
    {
        var json = JsonSerializer.SerializeToNode(args, _json)?.AsObject();
        if (json is null)
        {
            return "{}";
        }

        foreach (var key in json.Select(p => p.Key).ToList())
        {
            var value = json[key]?.ToString();
            json[key] = key.Equals("email", StringComparison.OrdinalIgnoreCase) ? PiiMasker.MaskEmail(value) : PiiMasker.MaskText(value);
        }

        return json.ToJsonString();
    }

    public static string IdempotencyKey(Guid conversationId, params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts.Prepend(conversationId.ToString("N")))));
        return $"{parts[0]}-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static object OrderView(OrderDetail o) => new
    {
        o.Number,
        o.Status,
        placed = o.PlacedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        o.Total,
        items = o.Lines.Select(l => new { l.Sku, l.Name, l.Category, l.Quantity, l.UnitPrice, l.ReturnedQuantity }),
        delivery = o.Delivery is null ? null : new
        {
            o.Delivery.Carrier,
            o.Delivery.TrackingNumber,
            delivered = o.Delivery.DeliveredAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            estimated = o.Delivery.EstimatedDelivery?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        },
        returns = o.Returns.Select(r => new { r.Number, r.Sku, r.Quantity, r.Status }),
    };

    private const string SummaryMarker = "\u0001";

    private static string Ok(object payload, string summary) => SummaryMarker + summary + SummaryMarker + JsonSerializer.Serialize(payload, _json);

    private static string Fail(string error, string message) => JsonSerializer.Serialize(new { error, message }, _json);

    private static string NotVerified() =>
        Fail("not_verified", "The customer isn't verified in this chat. Ask for the order number and the email on the order, then call get_order_status.");

    private static string ExtractSummary(ref string result)
    {
        if (!result.StartsWith(SummaryMarker, StringComparison.Ordinal))
        {
            return TryGetError(result) ?? "error";
        }

        var end = result.IndexOf(SummaryMarker, 1, StringComparison.Ordinal);
        var summary = result[1..end];
        result = result[(end + 1)..];
        return summary;
    }

    private static string? TryGetError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ErrorMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Not allowed." : "Not allowed.";
    }

    private static string ConditionLabel(ItemCondition c) => c switch
    {
        ItemCondition.Defective => "Defect",
        ItemCondition.Unopened => "Unopened",
        _ => "Opened",
    };

    private static string NormalizeOrderNumber(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        return v.StartsWith("BL", StringComparison.Ordinal) && !v.StartsWith("BL-", StringComparison.Ordinal) ? "BL-" + v[2..] : v;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"^BL-\d{5}$")]
    private static partial Regex OrderNumberRegex();
}
