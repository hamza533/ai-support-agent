using Azure.Monitor.OpenTelemetry.AspNetCore;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Brightline.Web.Infrastructure;

public static class Observability
{
    /// <summary>Activity sources and meters traced end to end: agent steps, tool calls, LLM calls, jobs.</summary>
    public static readonly string[] Sources =
    [
        "Brightline.*",
        "Experimental.Microsoft.Agents.AI*",
        "Microsoft.Agents.AI*",
        "Experimental.Microsoft.Extensions.AI*",
        "Microsoft.Extensions.AI*",
    ];

    public static WebApplicationBuilder AddBrightlineObservability(this WebApplicationBuilder builder)
    {
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "brightline-web";

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(Observability).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                .AddSource(Sources)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/_framework"))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(m => m
                .AddMeter(Sources)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        // Production: Application Insights. Local: OTLP to the Aspire Dashboard. (The two exporters are exclusive.)
        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            otel.UseAzureMonitor();
        }
        else if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
