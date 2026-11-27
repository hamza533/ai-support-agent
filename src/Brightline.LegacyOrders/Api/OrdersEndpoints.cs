using System.ComponentModel.DataAnnotations;
using Brightline.LegacyOrders.Data;
using Microsoft.EntityFrameworkCore;

namespace Brightline.LegacyOrders.Api;

public static class OrdersEndpoints
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const string AdminKeyHeader = "X-Admin-Key";
    public const string IdempotencyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapLegacyApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .AddEndpointFilter(new ApiKeyFilter(ApiKeyHeader, o => o.ApiKey))
            .WithTags("Orders & Returns");

        api.MapPost("/verifications", VerifyAsync)
            .WithSummary("Verify a customer by order number + the email on that order")
            .Produces<VerificationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/customers/{customerId:int}/orders", ListOrdersAsync)
            .WithSummary("List a customer's orders, newest first")
            .Produces<List<OrderSummaryDto>>();

        api.MapGet("/customers/{customerId:int}/orders/{orderNumber}", GetOrderAsync)
            .WithSummary("Order detail with lines, delivery and returns (404 if it is not this customer's order)")
            .Produces<OrderDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/customers/{customerId:int}/returns", ListReturnsAsync)
            .WithSummary("List a customer's return requests")
            .Produces<List<ReturnDto>>();

        api.MapPost("/customers/{customerId:int}/returns", CreateReturnAsync)
            .WithSummary("Create a return request. Requires an Idempotency-Key header; repeats return the original.")
            .Produces<ReturnDto>(StatusCodes.Status201Created)
            .Produces<ReturnDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        app.MapPost("/api/admin/reset", ResetAsync)
            .AddEndpointFilter(new ApiKeyFilter(AdminKeyHeader, o => o.AdminKey))
            .WithTags("Admin")
            .WithSummary("Demo only: wipe and re-seed all legacy data")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static async Task<IResult> VerifyAsync(VerificationRequest request, LegacyDbContext db, CancellationToken ct)
    {
        if (!TryValidate(request, out var problem))
        {
            return problem;
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var number = request.OrderNumber.Trim().ToUpperInvariant();
        var match = await db.Orders.AsNoTracking()
            .Where(o => o.Number == number && o.Customer!.Email == email)
            .Select(o => new { o.CustomerId, o.Customer!.Name })
            .SingleOrDefaultAsync(ct);

        // Deliberately the same response for "no such order" and "wrong email".
        return match is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No order matches that order number and email.")
            : Results.Ok(new VerificationResponse(match.CustomerId, match.Name.Split(' ')[0]));
    }

    private static async Task<IResult> ListOrdersAsync(int customerId, LegacyDbContext db, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.PlacedAt)
            .Select(o => new
            {
                o.Number,
                o.Status,
                o.PlacedAt,
                o.Total,
                ItemCount = o.Lines.Sum(l => l.Quantity),
                DeliveredAt = o.Delivery != null ? o.Delivery.DeliveredAt : null,
            })
            .ToListAsync(ct);

        return Results.Ok(orders.Select(o => new OrderSummaryDto(
            o.Number, o.Status.ToString(), o.PlacedAt, o.Total, o.ItemCount, o.DeliveredAt)).ToList());
    }

    private static async Task<IResult> GetOrderAsync(int customerId, string orderNumber, LegacyDbContext db, CancellationToken ct)
    {
        var order = await LoadOrderAsync(db, customerId, orderNumber, track: false, ct);
        return order is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found.")
            : Results.Ok(ToDetail(order));
    }

    private static async Task<IResult> ListReturnsAsync(int customerId, LegacyDbContext db, CancellationToken ct)
    {
        var returns = await db.Returns.AsNoTracking()
            .Where(r => r.CustomerId == customerId)
            .OrderByDescending(r => r.CreatedAt)
            .Include(r => r.OrderLine!).ThenInclude(l => l.Order)
            .ToListAsync(ct);
        return Results.Ok(returns.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateReturnAsync(
        int customerId,
        CreateReturnRequest request,
        HttpRequest http,
        LegacyDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var key = http.Headers[IdempotencyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"{IdempotencyHeader} header (1-100 characters) is required.");
        }

        if (!TryValidate(request, out var problem))
        {
            return problem;
        }

        var existing = await FindByKeyAsync(db, customerId, key, ct);
        if (existing is not null)
        {
            return Results.Ok(ToDto(existing));
        }

        var order = await LoadOrderAsync(db, customerId, request.OrderNumber, track: true, ct);
        if (order is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found.");
        }

        if (order.Status != OrderStatus.Delivered)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Only delivered orders can be returned.");
        }

        var line = order.Lines.SingleOrDefault(l => string.Equals(l.Sku, request.Sku, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Item not found on this order.");
        }

        // Lock the order line so concurrent requests (different idempotency keys) can't together return more than was bought.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM order_lines WHERE id = {line.Id} FOR UPDATE", ct);
        var alreadyReturned = await db.Returns
            .Where(r => r.OrderLineId == line.Id && r.Status != ReturnStatus.Rejected)
            .SumAsync(r => r.Quantity, ct);
        var remaining = line.Quantity - alreadyReturned;
        if (request.Quantity > remaining)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: $"Only {remaining} of this item can still be returned.");
        }

        var entity = new ReturnRequest
        {
            CustomerId = customerId,
            OrderLineId = line.Id,
            Quantity = request.Quantity,
            Reason = request.Reason.Trim(),
            Status = ReturnStatus.Requested,
            CreatedAt = time.GetUtcNow(),
            IdempotencyKey = key,
        };
        db.Returns.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent request with the same idempotency key won the race: return its result.
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var winner = await FindByKeyAsync(db, customerId, key, ct);
            if (winner is not null)
            {
                return Results.Ok(ToDto(winner));
            }

            throw;
        }

        var created = await FindByKeyAsync(db, customerId, key, ct);
        var dto = ToDto(created!);
        return Results.Created($"/api/customers/{customerId}/returns/{dto.Number}", dto);
    }

    private static async Task<IResult> ResetAsync(LegacySeeder seeder, CancellationToken ct)
    {
        await seeder.ResetAsync(ct);
        return Results.NoContent();
    }

    private static Task<ReturnRequest?> FindByKeyAsync(LegacyDbContext db, int customerId, string key, CancellationToken ct) =>
        db.Returns.AsNoTracking()
            .Include(r => r.OrderLine!).ThenInclude(l => l.Order)
            .SingleOrDefaultAsync(r => r.CustomerId == customerId && r.IdempotencyKey == key, ct);

    private static Task<Order?> LoadOrderAsync(LegacyDbContext db, int customerId, string orderNumber, bool track, CancellationToken ct)
    {
        IQueryable<Order> query = db.Orders
            .Include(o => o.Lines).ThenInclude(l => l.Returns)
            .Include(o => o.Delivery)
            .AsSplitQuery();
        if (!track)
        {
            query = query.AsNoTracking();
        }

        var number = orderNumber.Trim().ToUpperInvariant();
        return query.SingleOrDefaultAsync(o => o.Number == number && o.CustomerId == customerId, ct);
    }

    private static OrderDetailDto ToDetail(Order o) => new(
        o.Number,
        o.Status.ToString(),
        o.PlacedAt,
        o.Total,
        [.. o.Lines.OrderBy(l => l.Id).Select(l => new OrderLineDto(
            l.Id,
            l.Sku,
            l.Name,
            l.Category,
            l.Quantity,
            l.UnitPrice,
            l.Returns.Where(r => r.Status != ReturnStatus.Rejected).Sum(r => r.Quantity)))],
        o.Delivery is null
            ? null
            : new DeliveryDto(o.Delivery.Carrier, o.Delivery.TrackingNumber, o.Delivery.ShippedAt, o.Delivery.DeliveredAt, o.Delivery.EstimatedDelivery),
        [.. o.Lines.SelectMany(l => l.Returns.Select(r => new ReturnDto(
            r.Number, o.Number, l.Sku, r.Quantity, r.Reason, r.Status.ToString(), r.CreatedAt)))]);

    private static ReturnDto ToDto(ReturnRequest r) => new(
        r.Number,
        r.OrderLine?.Order?.Number ?? string.Empty,
        r.OrderLine?.Sku ?? string.Empty,
        r.Quantity,
        r.Reason,
        r.Status.ToString(),
        r.CreatedAt);

    private static bool TryValidate(object model, out IResult problem)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true))
        {
            problem = Results.Empty;
            return true;
        }

        problem = Results.ValidationProblem(results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty).Select(m => (Member: m, Error: r.ErrorMessage ?? "Invalid value.")))
            .GroupBy(x => x.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Error).ToArray()));
        return false;
    }
}
