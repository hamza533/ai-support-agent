using Brightline.LegacyOrders.Client;

namespace Brightline.Testing;

/// <summary>
/// In-memory stand-in for the legacy Orders API with the demo story: Maya (id 1) and Daniel (id 2).
/// Honours the same contract: orders are only returned for their owner; returns are idempotent per key.
/// </summary>
public sealed class FakeLegacyOrdersClient : ILegacyOrdersClient
{
    public const int MayaId = 1;
    public const int DanielId = 2;
    public const string MayaEmail = "maya.t@example.com";
    public const string DanielEmail = "daniel.o@example.com";

    private readonly Dictionary<int, (string Name, string Email)> _customers = new()
    {
        [MayaId] = ("Maya Thompson", MayaEmail),
        [DanielId] = ("Daniel Okafor", DanielEmail),
    };

    private readonly List<(int CustomerId, OrderDetail Order)> _orders;
    private int _nextReturn = 2291;

    public FakeLegacyOrdersClient(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        _orders =
        [
            (MayaId, Order("BL-10482", t.AddDays(-13), t.AddDays(-9), ("AUD-AURORA-WH", "Aurora Wireless Headphones", "Audio", 149m, 1), ("ACC-USBC-65W", "USB-C Charger 65W", "Accessories", 39m, 1))),
            (MayaId, Order("BL-10317", t.AddDays(-50), t.AddDays(-45), ("SMH-HUB-MINI", "Home Hub Mini", "Smart Home", 89m, 1))),
            (DanielId, Order("BL-10555", t.AddDays(-8), t.AddDays(-4), ("LAP-NOVA-14", "Nova 14 Laptop", "Laptops", 1099m, 1), ("ACC-SLEEVE-14", "Laptop Sleeve 14\"", "Accessories", 29m, 1))),
            (DanielId, Order("BL-10600", t.AddDays(-5), t.AddDays(-3), ("SFT-SHIELD-1Y", "Shield Antivirus (1-year licence)", "Software", 39m, 1))),
            (DanielId, Order("BL-10611", t.AddDays(-1), null, ("WEA-STRIDE-W", "Stride Smartwatch", "Wearables", 249m, 1))),
        ];
    }

    /// <summary>Simulates the legacy API being down (circuit open / unreachable).</summary>
    public bool Unavailable { get; set; }

    public List<(int CustomerId, NewReturn Request, string Key, ReturnInfo Created)> Returns { get; } = [];

    /// <summary>Every customer id the agent passed to the API (to prove forged ids never reach it).</summary>
    public List<int> CustomerIdsRequested { get; } = [];

    public Task<VerifiedCustomer?> VerifyCustomerAsync(string orderNumber, string email, CancellationToken ct = default)
    {
        ThrowIfDown();
        var match = _orders.FirstOrDefault(o => o.Order.Number == orderNumber.Trim().ToUpperInvariant());
        if (match.Order is null || !string.Equals(_customers[match.CustomerId].Email, email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<VerifiedCustomer?>(null);
        }

        return Task.FromResult<VerifiedCustomer?>(new VerifiedCustomer(match.CustomerId, _customers[match.CustomerId].Name.Split(' ')[0]));
    }

    public Task<IReadOnlyList<OrderSummary>> ListOrdersAsync(int customerId, CancellationToken ct = default)
    {
        ThrowIfDown();
        CustomerIdsRequested.Add(customerId);
        IReadOnlyList<OrderSummary> list = [.. _orders.Where(o => o.CustomerId == customerId).Select(o => new OrderSummary(
            o.Order.Number, o.Order.Status, o.Order.PlacedAt, o.Order.Total, o.Order.Lines.Sum(l => l.Quantity), o.Order.Delivery?.DeliveredAt))];
        return Task.FromResult(list);
    }

    public Task<OrderDetail?> GetOrderAsync(int customerId, string orderNumber, CancellationToken ct = default)
    {
        ThrowIfDown();
        CustomerIdsRequested.Add(customerId);
        var order = _orders.FirstOrDefault(o => o.CustomerId == customerId && o.Order.Number == orderNumber.Trim().ToUpperInvariant()).Order;
        return Task.FromResult(order is null ? null : WithReturns(order));
    }

    public Task<CreateReturnResult> CreateReturnAsync(int customerId, NewReturn request, string idempotencyKey, CancellationToken ct = default)
    {
        ThrowIfDown();
        CustomerIdsRequested.Add(customerId);
        var existing = Returns.FirstOrDefault(r => r.CustomerId == customerId && r.Key == idempotencyKey);
        if (existing.Created is not null)
        {
            return Task.FromResult(new CreateReturnResult(existing.Created, false));
        }

        var order = _orders.FirstOrDefault(o => o.CustomerId == customerId && o.Order.Number == request.OrderNumber).Order
            ?? throw new LegacyOrdersRejectedException("Order not found.");
        var line = order.Lines.FirstOrDefault(l => l.Sku == request.Sku) ?? throw new LegacyOrdersRejectedException("Item not found on this order.");
        var already = Returns.Where(r => r.Request.OrderNumber == order.Number && r.Request.Sku == line.Sku).Sum(r => r.Request.Quantity);
        if (request.Quantity > line.Quantity - already)
        {
            throw new LegacyOrdersRejectedException($"Only {line.Quantity - already} of this item can still be returned.");
        }

        var created = new ReturnInfo($"RR-{_nextReturn++}", order.Number, line.Sku, request.Quantity, request.Reason, "Requested", DateTimeOffset.UtcNow);
        Returns.Add((customerId, request, idempotencyKey, created));
        return Task.FromResult(new CreateReturnResult(created, true));
    }

    private OrderDetail WithReturns(OrderDetail order)
    {
        var returns = Returns.Where(r => r.Request.OrderNumber == order.Number).Select(r => r.Created).ToList();
        var lines = order.Lines.Select(l => l with { ReturnedQuantity = returns.Where(r => r.Sku == l.Sku).Sum(r => r.Quantity) }).ToList();
        return order with { Lines = lines, Returns = returns };
    }

    private void ThrowIfDown()
    {
        if (Unavailable)
        {
            throw new LegacyOrdersUnavailableException("The order system is temporarily unavailable (circuit open).");
        }
    }

    private static int _lineIds;

    private static OrderDetail Order(string number, DateTimeOffset placed, DateTimeOffset? delivered, params (string Sku, string Name, string Category, decimal Price, int Qty)[] lines) =>
        new(
            number,
            delivered is null ? "Shipped" : "Delivered",
            placed,
            lines.Sum(l => l.Price * l.Qty),
            [.. lines.Select(l => new OrderLine(Interlocked.Increment(ref _lineIds), l.Sku, l.Name, l.Category, l.Qty, l.Price, 0))],
            new DeliveryInfo("ParcelLine", "TRK123456789", placed.AddDays(1), delivered, DateOnly.FromDateTime((delivered ?? placed.AddDays(5)).UtcDateTime)),
            []);
}
