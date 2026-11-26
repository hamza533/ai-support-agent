using System.ComponentModel.DataAnnotations;

namespace Brightline.LegacyOrders.Api;

public sealed record VerificationRequest(
    [property: Required, RegularExpression(@"^BL-\d{5}$")] string OrderNumber,
    [property: Required, EmailAddress, MaxLength(320)] string Email);

public sealed record VerificationResponse(int CustomerId, string FirstName);

public sealed record OrderSummaryDto(
    string Number,
    string Status,
    DateTimeOffset PlacedAt,
    decimal Total,
    int ItemCount,
    DateTimeOffset? DeliveredAt);

public sealed record OrderLineDto(
    int LineId,
    string Sku,
    string Name,
    string Category,
    int Quantity,
    decimal UnitPrice,
    int ReturnedQuantity);

public sealed record DeliveryDto(
    string Carrier,
    string TrackingNumber,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateOnly? EstimatedDelivery);

public sealed record ReturnDto(
    string Number,
    string OrderNumber,
    string Sku,
    int Quantity,
    string Reason,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record OrderDetailDto(
    string Number,
    string Status,
    DateTimeOffset PlacedAt,
    decimal Total,
    IReadOnlyList<OrderLineDto> Lines,
    DeliveryDto? Delivery,
    IReadOnlyList<ReturnDto> Returns);

public sealed record CreateReturnRequest(
    [property: Required, RegularExpression(@"^BL-\d{5}$")] string OrderNumber,
    [property: Required, MaxLength(40)] string Sku,
    [property: Range(1, 20)] int Quantity,
    [property: Required, MinLength(3), MaxLength(500)] string Reason);
