using Brightline.Agent;
using Brightline.Data.Entities;
using Microsoft.Extensions.Options;

namespace Brightline.Web.Infrastructure;

/// <summary>Public demo settings beyond the daily conversation cap (<see cref="DemoOptions"/>).</summary>
public sealed class DemoAccessOptions
{
    public const string Section = "Demo";

    /// <summary>Read-only staff account published in the README (role staff: can't change settings, delete or add users).</summary>
    public string StaffEmail { get; set; } = string.Empty;
    public string StaffPassword { get; set; } = string.Empty;

    /// <summary>UTC hour of the nightly legacy data reset.</summary>
    public int ResetHourUtc { get; set; } = 3;
}

/// <summary>Creates the demo staff user on start-up when demo mode is on.</summary>
public sealed class DemoSeeder(
    IOptions<DemoOptions> demo,
    IOptions<DemoAccessOptions> access,
    IdentityBootstrapper identity,
    ILogger<DemoSeeder> logger) : IStartupSeeder
{
    public async Task SeedAsync(CancellationToken ct)
    {
        var a = access.Value;
        if (!demo.Value.Enabled || string.IsNullOrWhiteSpace(a.StaffEmail) || string.IsNullOrWhiteSpace(a.StaffPassword))
        {
            return;
        }

        await identity.CreateUserAsync(a.StaffEmail, Roles.Staff, a.StaffPassword, "Demo staff");
        logger.LogInformation("Demo staff account ready (read-only role)");
    }
}

/// <summary>Demo only: resets the legacy Orders data nightly through the legacy API's admin endpoint (HTTP, like everything else).</summary>
public sealed class DemoResetService(
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    IOptions<DemoAccessOptions> access,
    TimeProvider time,
    ILogger<DemoResetService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextReset(time.GetUtcNow(), access.Value.ResetHourUtc);
            try
            {
                await Task.Delay(delay, time, stoppingToken);
                await ResetAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Nightly demo reset failed; will retry tomorrow");
            }
        }
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        using var http = httpFactory.CreateClient("legacy-admin");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(configuration["LegacyOrders:BaseUrl"]!.TrimEnd('/') + "/"), "api/admin/reset"));
        request.Headers.Add("X-Admin-Key", configuration["LegacyOrders:AdminKey"]);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        logger.LogInformation("Nightly demo reset of legacy order data done");
    }

    public static TimeSpan DelayUntilNextReset(DateTimeOffset now, int hourUtc)
    {
        var next = new DateTimeOffset(now.UtcDateTime.Date.AddHours(hourUtc), TimeSpan.Zero);
        if (next <= now)
        {
            next = next.AddDays(1);
        }

        return next - now;
    }
}

public static class DemoModeExtensions
{
    public static IServiceCollection AddBrightlineDemoMode(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DemoAccessOptions>(configuration.GetSection(DemoAccessOptions.Section));
        services.AddScoped<IStartupSeeder, DemoSeeder>();
        if (configuration.GetValue<bool>("Demo:Enabled") && !string.IsNullOrWhiteSpace(configuration["LegacyOrders:AdminKey"]))
        {
            services.AddHttpClient("legacy-admin", c => c.Timeout = TimeSpan.FromSeconds(30));
            services.AddHostedService<DemoResetService>();
        }

        return services;
    }
}
