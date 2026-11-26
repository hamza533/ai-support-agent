using Brightline.Agent;
using Brightline.LegacyOrders.Client;

namespace Brightline.Web.Components.Chat;

public enum ApprovalViewState
{
    Pending,
    Confirmed,
    Cancelled,
}

/// <summary>View models for what the chat shows, in order.</summary>
public abstract class ChatItem
{
    public Guid Key { get; } = Guid.NewGuid();
}

public sealed class UserItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

public sealed class AssistantItem : ChatItem
{
    public string Text { get; set; } = string.Empty;

    public IReadOnlyList<Citation> Citations { get; set; } = [];

    public bool Streaming { get; set; } = true;

    public bool OfferTicket { get; set; }

    public List<StepView> Steps { get; } = [];
}

public sealed class StepView(string callId, string label)
{
    public string CallId { get; } = callId;

    public string Label { get; } = label;

    public string Status { get; set; } = "running";
}

public sealed class OrderItem(OrderDetail order) : ChatItem
{
    public OrderDetail Order { get; } = order;
}

public sealed class ApprovalItem(ApprovalCard card) : ChatItem
{
    public ApprovalCard Card { get; } = card;

    public ApprovalViewState State { get; set; }
}

public sealed class ActionItem(ActionCompleted action) : ChatItem
{
    public ActionCompleted Action { get; } = action;
}

public sealed class ErrorItem(ChatError error) : ChatItem
{
    public ChatError Error { get; } = error;
}
