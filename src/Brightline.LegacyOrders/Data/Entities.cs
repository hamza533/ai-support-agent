namespace Brightline.LegacyOrders.Data;

public sealed class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public string? Phone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Order> Orders { get; set; } = [];
}

public enum OrderStatus
{
    Placed,
    Shipped,
    Delivered,
    Cancelled,
}

public sealed class Order
{
    public int Id { get; set; }
    public required string Number { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public OrderStatus Status { get; set; }
    public decimal Total { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
    public Delivery? Delivery { get; set; }
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public List<ReturnRequest> Returns { get; set; } = [];
}

public sealed class Delivery
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public required string Carrier { get; set; }
    public required string TrackingNumber { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateOnly? EstimatedDelivery { get; set; }
}

public enum ReturnStatus
{
    Requested,
    Received,
    Refunded,
    Rejected,
}

public sealed class ReturnRequest
{
    public int Id { get; set; }
    /// <summary>Assigned by the database (RR-####); left null so EF uses the column default.</summary>
    public string Number { get; set; } = null!;
    public int CustomerId { get; set; }
    public int OrderLineId { get; set; }
    public OrderLine? OrderLine { get; set; }
    public int Quantity { get; set; }
    public required string Reason { get; set; }
    public ReturnStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string IdempotencyKey { get; set; }
}
