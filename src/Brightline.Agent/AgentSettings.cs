using System.Globalization;
using Brightline.Data;

namespace Brightline.Agent;

/// <summary>Admin-editable agent and policy settings (defaults below, overrides in the <c>settings</c> table).</summary>
public sealed record AgentSettings
{
    public string SystemPrompt { get; init; } = Prompts.DefaultSystemPrompt;
    public float Temperature { get; init; } = 0.2f;
    public int ReturnWindowDays { get; init; } = 30;
    public IReadOnlyList<string> EligibleCategories { get; init; } =
        ["Audio", "Accessories", "Laptops", "Phones", "Wearables", "Smart Home", "Gaming", "Monitors"];
    public int MaxItemsPerReturn { get; init; } = 5;

    /// <summary>Locked ON: actions that change data always need the customer's confirmation (SPEC section 7).</summary>
    public bool RequireApprovalForReturns => true;

    /// <summary>Locked ON.</summary>
    public bool RequireApprovalForTickets => true;

    public static class Keys
    {
        public const string SystemPrompt = "agent.systemPrompt";
        public const string Temperature = "agent.temperature";
        public const string ReturnWindowDays = "policy.returnWindowDays";
        public const string EligibleCategories = "policy.eligibleCategories";
        public const string MaxItemsPerReturn = "policy.maxItemsPerReturn";
    }
}

public interface IAgentSettingsProvider
{
    Task<AgentSettings> GetAsync(CancellationToken ct = default);
}

public sealed class AgentSettingsService(SettingsStore store) : IAgentSettingsProvider
{
    public async Task<AgentSettings> GetAsync(CancellationToken ct = default)
    {
        var values = await store.GetAllAsync(ct);
        var defaults = new AgentSettings();
        return new AgentSettings
        {
            SystemPrompt = Get(values, AgentSettings.Keys.SystemPrompt) is { Length: > 0 } prompt ? prompt : defaults.SystemPrompt,
            Temperature = float.TryParse(Get(values, AgentSettings.Keys.Temperature), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                ? Math.Clamp(t, 0f, 1f) : defaults.Temperature,
            ReturnWindowDays = int.TryParse(Get(values, AgentSettings.Keys.ReturnWindowDays), out var w) && w is > 0 and <= 365
                ? w : defaults.ReturnWindowDays,
            EligibleCategories = Get(values, AgentSettings.Keys.EligibleCategories) is { Length: > 0 } cats
                ? [.. cats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
                : defaults.EligibleCategories,
            MaxItemsPerReturn = int.TryParse(Get(values, AgentSettings.Keys.MaxItemsPerReturn), out var m) && m is > 0 and <= 20
                ? m : defaults.MaxItemsPerReturn,
        };
    }

    public Task SaveAsync(AgentSettings settings, string? updatedBy, CancellationToken ct = default) =>
        store.SetManyAsync(new Dictionary<string, string>
        {
            [AgentSettings.Keys.SystemPrompt] = settings.SystemPrompt,
            [AgentSettings.Keys.Temperature] = settings.Temperature.ToString(CultureInfo.InvariantCulture),
            [AgentSettings.Keys.ReturnWindowDays] = settings.ReturnWindowDays.ToString(CultureInfo.InvariantCulture),
            [AgentSettings.Keys.EligibleCategories] = string.Join(", ", settings.EligibleCategories),
            [AgentSettings.Keys.MaxItemsPerReturn] = settings.MaxItemsPerReturn.ToString(CultureInfo.InvariantCulture),
        }, updatedBy, ct);

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;
}

public sealed class DemoOptions
{
    public const string Section = "Demo";
    public bool Enabled { get; set; }
    public int DailyConversations { get; set; } = 40;
}
