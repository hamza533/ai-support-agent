namespace Brightline.LegacyOrders.Client;

// Consumer-side contracts for the legacy Orders & Returns API (as a client team would generate from its OpenAPI).
// Deliberately NOT shared with Brightline.LegacyOrders: the app only knows the HTTP contract.

public sealed record VerifiedCustomer(int CustomerId, string FirstName);

public sealed record OrderSummary(
    string Number,
    string Status,
    DateTimeOffset PlacedAt,
    decimal Total,
    int ItemCount,
    DateTimeOffset? DeliveredAt);

public sealed record OrderLine(
    int LineId,
    string Sku,
    string Name,
    string Category,
    int Quantity,
    decimal UnitPrice,
    int ReturnedQuantity)
{
    public int ReturnableQuantity => Math.Max(0, Quantity - ReturnedQuantity);
}

public sealed record DeliveryInfo(
    string Carrier,
    string TrackingNumber,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateOnly? EstimatedDelivery);

public sealed record ReturnInfo(
    string Number,
    string OrderNumber,
    string Sku,
    int Quantity,
    string Reason,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record OrderDetail(
    string Number,
    string Status,
    DateTimeOffset PlacedAt,
    decimal Total,
    IReadOnlyList<OrderLine> Lines,
    DeliveryInfo? Delivery,
    IReadOnlyList<ReturnInfo> Returns)
{
    public bool IsDelivered => string.Equals(Status, "Delivered", StringComparison.OrdinalIgnoreCase);
}

public sealed record NewReturn(string OrderNumber, string Sku, int Quantity, string Reason);

/// <summary>Result of creating a return: <see cref="Created"/> is false when the idempotency key matched an earlier request.</summary>
public sealed record CreateReturnResult(ReturnInfo Return, bool Created);
