using Brightline.Data;
using Brightline.Data.Jobs;
using Microsoft.AspNetCore.HttpOverrides;
using NeoUI.Blazor.Extensions;
using NeoUI.Blazor.Primitives.Extensions;

namespace Brightline.Web.Infrastructure;

public static class AppComposition
{
    public static IServiceCollection AddBrightlineApp(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment env, bool workerOnly = false)
    {
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Render / Container Apps terminate TLS at their proxy.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        services.AddBrightlineData(configuration);
        services.AddBrightlineAuth(configuration, env);
        services.AddBrightlineHealthChecks();
        services.AddBrightlineRateLimiting(configuration);
        services.AddScoped<IdentityBootstrapper>();
        services.AddBrightlineFeatures(configuration);

        var mode = configuration[$"{WorkerOptions.Section}:Mode"] ?? "embedded";
        if (workerOnly || string.Equals(mode, "embedded", StringComparison.OrdinalIgnoreCase))
        {
            services.AddBrightlineBackgroundWork();
        }

        services.AddNeoUIPrimitives();
        services.AddNeoUIComponents();
        services.AddRazorComponents().AddInteractiveServerComponents();
        return services;
    }
}
