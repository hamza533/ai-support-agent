using Brightline.Ai;
using Brightline.Knowledge;
using Brightline.LegacyOrders.Client;

namespace Brightline.Web.Infrastructure;

/// <summary>Feature registrations (AI, knowledge base, agent, chat, admin).</summary>
public static class Features
{
    public static IServiceCollection AddBrightlineFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLegacyOrdersClient(configuration);
        services.AddBrightlineAi(configuration);
        services.AddBrightlineKnowledge(configuration);
        services.AddScoped<KnowledgeSeeder>();
        services.AddScoped<ISeeder>(sp => sp.GetRequiredService<KnowledgeSeeder>());
        services.AddScoped<IStartupSeeder>(sp => sp.GetRequiredService<KnowledgeSeeder>());
        return services;
    }

    public static IEndpointRouteBuilder MapBrightlineEndpoints(this IEndpointRouteBuilder app) => app;
}
