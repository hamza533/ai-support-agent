using Brightline.LegacyOrders.Client;

namespace Brightline.Agent;

public enum ItemCondition
{
    Unopened,
    Defective,
    OpenedWorking,
}

public sealed record EligibilityResult(
    bool Eligible,
    IReadOnlyList<string> Reasons,
    int? DaysSinceDelivery,
    int WindowDays,
    string Category,
    int ReturnableQuantity)
{
    public string Summary => (Eligible ? "Eligible" : "Not eligible") + " — " + string.Join(", ", Reasons);
}

/// <summary>
/// The return policy as code. Used by <c>check_return_eligibility</c> and re-checked on the server by
/// <c>create_return_request</c> before anything is written (the model's word is never enough).
/// </summary>
public static class ReturnPolicy
{
    public static EligibilityResult Evaluate(OrderDetail order, OrderLine line, ItemCondition condition, AgentSettings settings, DateTimeOffset now)
    {
        var reasons = new List<string>();
        var eligible = true;
        int? days = null;

        if (!order.IsDelivered || order.Delivery?.DeliveredAt is null)
        {
            eligible = false;
            reasons.Add($"order status is {order.Status}; only delivered orders can be returned");
        }
        else
        {
            days = (int)Math.Floor((now - order.Delivery.DeliveredAt.Value).TotalDays);
            if (days > settings.ReturnWindowDays)
            {
                eligible = false;
                reasons.Add($"delivered {days} days ago, outside the {settings.ReturnWindowDays}-day window");
            }
            else
            {
                reasons.Add($"delivered {days} days ago, within the {settings.ReturnWindowDays}-day window");
            }
        }

        if (!settings.EligibleCategories.Contains(line.Category, StringComparer.OrdinalIgnoreCase))
        {
            eligible = false;
            reasons.Add($"{line.Category} items can't be returned");
        }

        switch (condition)
        {
            case ItemCondition.Defective:
                reasons.Add("defect reported");
                break;
            case ItemCondition.Unopened:
                reasons.Add("unopened");
                break;
            default:
                eligible = false;
                reasons.Add("opened items that work can't be returned for a change of mind");
                break;
        }

        if (line.ReturnableQuantity <= 0)
        {
            eligible = false;
            reasons.Add("this item has already been returned");
        }

        return new EligibilityResult(eligible, reasons, days, settings.ReturnWindowDays, line.Category, line.ReturnableQuantity);
    }

    public static ItemCondition ParseCondition(string? condition) => condition?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
    {
        "unopened" or "sealed" or "new" => ItemCondition.Unopened,
        "defective" or "faulty" or "broken" or "defect" => ItemCondition.Defective,
        _ => ItemCondition.OpenedWorking,
    };
}
