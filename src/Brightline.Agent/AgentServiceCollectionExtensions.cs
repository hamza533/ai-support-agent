using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>Registers the agent. <see cref="ChatService"/> is scoped: one per Blazor circuit = one conversation.</summary>
    public static IServiceCollection AddBrightlineAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DemoOptions>(configuration.GetSection(DemoOptions.Section));
        services.AddScoped<AgentSettingsService>();
        services.AddScoped<IAgentSettingsProvider>(sp => sp.GetRequiredService<AgentSettingsService>());
        services.AddScoped<IConversationStore, ConversationStore>();
        services.AddScoped<ChatService>();
        return services;
    }
}
