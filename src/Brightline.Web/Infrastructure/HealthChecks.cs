using Brightline.Data;
using Brightline.LegacyOrders.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Brightline.Web.Infrastructure;

/// <summary>Readiness: the legacy Orders API answers its own /health. Degraded (not unhealthy) when down: chat still works for documents.</summary>
public sealed class LegacyApiHealthCheck(IHttpClientFactory httpFactory, IOptions<LegacyOrdersClientOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = httpFactory.CreateClient("health");
            http.Timeout = TimeSpan.FromSeconds(3);
            using var response = await http.GetAsync(new Uri(new Uri(options.Value.BaseUrl.TrimEnd('/') + "/"), "health"), cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded($"Legacy Orders API returned {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Degraded("Legacy Orders API unreachable");
        }
    }
}

public static class HealthCheckExtensions
{
    public static IServiceCollection AddBrightlineHealthChecks(this IServiceCollection services)
    {
        services.AddHttpClient("health");
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: ["ready"])
            .AddCheck<LegacyApiHealthCheck>("legacy-orders-api", tags: ["ready"]);
        return services;
    }

    public static IEndpointRouteBuilder MapBrightlineHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health", new() { Predicate = _ => false }); // liveness: the process answers
        app.MapHealthChecks("/ready", new()
        {
            Predicate = c => c.Tags.Contains("ready"),
            ResponseWriter = async (ctx, report) =>
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
                });
            },
        });
        return app;
    }
}
