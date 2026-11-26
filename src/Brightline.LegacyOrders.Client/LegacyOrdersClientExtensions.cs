using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Brightline.LegacyOrders.Client;

public sealed class LegacyOrdersClientOptions
{
    public const string Section = "LegacyOrders";

    [Required, Url]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    public string ApiKey { get; set; } = string.Empty;
}

public static class LegacyOrdersClientExtensions
{
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>
    /// Registers <see cref="ILegacyOrdersClient"/> as a typed HttpClient with the standard resilience pipeline:
    /// rate limiter → total timeout (20 s) → retry (3×, exponential + jitter) → circuit breaker → attempt timeout (5 s).
    /// POST retries are safe because return creation carries an Idempotency-Key.
    /// Override any value under <c>LegacyOrders:Resilience</c> (HttpStandardResilienceOptions).
    /// </summary>
    public static IHttpClientBuilder AddLegacyOrdersClient(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(LegacyOrdersClientOptions.Section);
        services.AddOptions<LegacyOrdersClientOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var builder = services.AddHttpClient<ILegacyOrdersClient, LegacyOrdersClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<LegacyOrdersClientOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add(ApiKeyHeader, options.ApiKey);
            http.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
        });

        builder.AddStandardResilienceHandler(o =>
        {
            ConfigureDefaults(o);
            section.GetSection("Resilience").Bind(o);
        });

        return builder;
    }

    public static void ConfigureDefaults(HttpStandardResilienceOptions o)
    {
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        o.Retry.MaxRetryAttempts = 3;
        o.Retry.Delay = TimeSpan.FromMilliseconds(300);
        o.Retry.UseJitter = true;
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        o.CircuitBreaker.MinimumThroughput = 5;
        o.CircuitBreaker.FailureRatio = 0.5;
        o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
    }
}
