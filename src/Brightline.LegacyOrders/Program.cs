using System.Text.Json.Serialization;
using Brightline.LegacyOrders.Api;
using Brightline.LegacyOrders.Data;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LegacyOptions>(builder.Configuration.GetSection(LegacyOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<LegacyDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("LegacyOrders")));
builder.Services.AddScoped<LegacySeeder>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<LegacyDbContext>("database", tags: ["ready"]);
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info = new OpenApiInfo
        {
            Title = "Brightline Orders & Returns API (legacy)",
            Version = "v1",
            Description = "Stand-in for Brightline's existing order system. Secured with an X-Api-Key header.",
        };
        doc.Components ??= new OpenApiComponents();
        doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        doc.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = OrdersEndpoints.ApiKeyHeader,
        };
        doc.Security ??= [];
        doc.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", doc)] = [] });
        return Task.CompletedTask;
    });
});

var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "legacy-orders-api"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddNpgsql())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation());
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
});
if (!string.IsNullOrWhiteSpace(otlp))
{
    otel.UseOtlpExporter();
}

var app = builder.Build();

// One-shot commands for deploy steps and the nightly demo reset: --migrate, --seed, --reset
if (args.Contains("--migrate") || args.Contains("--seed") || args.Contains("--reset"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<LegacyDbContext>();
    var seeder = scope.ServiceProvider.GetRequiredService<LegacySeeder>();
    if (args.Contains("--migrate"))
    {
        await db.Database.MigrateAsync();
    }

    if (args.Contains("--reset"))
    {
        await seeder.ResetAsync();
    }
    else if (args.Contains("--seed"))
    {
        await seeder.SeedIfEmptyAsync();
    }

    return;
}

var options = app.Services.GetRequiredService<IOptions<LegacyOptions>>().Value;
if (options.MigrateOnStartup)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<LegacyDbContext>().Database.MigrateAsync();
    if (options.SeedOnStartup)
    {
        await scope.ServiceProvider.GetRequiredService<LegacySeeder>().SeedIfEmptyAsync();
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Brightline Orders & Returns API"));
app.MapHealthChecks("/health", new() { Predicate = _ => false });
app.MapHealthChecks("/ready", new() { Predicate = c => c.Tags.Contains("ready") });
app.MapLegacyApi();

await app.RunAsync();

namespace Brightline.LegacyOrders
{
    /// <summary>Entry point, exposed for WebApplicationFactory in tests.</summary>
    public partial class Program;
}
