using Brightline.LegacyOrders.Client;

namespace Brightline.Web.Infrastructure;

/// <summary>Feature registrations (AI, knowledge base, agent, chat, admin).</summary>
public static class Features
{
    public static IServiceCollection AddBrightlineFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLegacyOrdersClient(configuration);
        return services;
    }

    public static IEndpointRouteBuilder MapBrightlineEndpoints(this IEndpointRouteBuilder app) => app;
}
