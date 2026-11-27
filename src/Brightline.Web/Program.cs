using Brightline.Web.Components;
using Brightline.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// --worker: run only the background job worker (WORKER_MODE=separate deployments).
var workerOnly = args.Contains("--worker");

builder.AddBrightlineObservability();
builder.Services.AddBrightlineApp(builder.Configuration, builder.Environment, workerOnly);

var app = builder.Build();

if (Commands.IsCommand(args))
{
    return await Commands.RunAsync(app, args);
}

await Commands.MigrateIfConfiguredAsync(app);

if (workerOnly)
{
    app.MapBrightlineHealthChecks();
    await app.RunAsync();
    return 0;
}

// Behind Render / Container Apps the platform proxy sets X-Forwarded-*; only trust it when configured, otherwise a
// client could spoof its IP to dodge the per-IP rate limits.
if (app.Configuration.GetValue<bool>("Security:TrustForwardedHeaders"))
{
    app.UseForwardedHeaders();
}
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
    if (app.Configuration.GetValue("Security:HttpsRedirection", true))
    {
        app.UseHttpsRedirection();
    }
}

app.UseBrightlineSecurityHeaders();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapBrightlineHealthChecks();
app.MapExternalLogin();
app.MapBrightlineEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;

/// <summary>Entry point, exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
