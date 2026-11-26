using System.Globalization;
using Brightline.Data.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using NeoUI.Blazor;

namespace Brightline.Web.Components.Admin;

/// <summary>Shared helpers for admin pages: role checks (staff can read, admin can change settings/delete).</summary>
public abstract class AdminPageBase : ComponentBase
{
    protected static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    protected bool IsAdmin { get; private set; }

    protected string? UserName { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        if (AuthState is not null)
        {
            var user = (await AuthState).User;
            IsAdmin = user.IsInRole(Roles.Admin);
            UserName = user.Identity?.Name;
        }

        await LoadAsync();
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : at.ToString("MMM d", Us);
    }

    protected static BadgeVariant PriorityVariant(TicketPriority p) => p switch
    {
        TicketPriority.Urgent or TicketPriority.High => BadgeVariant.DangerSoft,
        TicketPriority.Normal => BadgeVariant.InfoSoft,
        _ => BadgeVariant.Muted,
    };

    protected static BadgeVariant StatusVariant(TicketStatus s) => s switch
    {
        TicketStatus.Open => BadgeVariant.WarningSoft,
        TicketStatus.InProgress => BadgeVariant.InfoSoft,
        _ => BadgeVariant.SuccessSoft,
    };

    protected static string StatusLabel(TicketStatus s) => s == TicketStatus.InProgress ? "In progress" : s.ToString();
}
