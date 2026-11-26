using System.Text.RegularExpressions;
using Brightline.Data.Entities;
using Brightline.Web.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.IntegrationTests.Infrastructure;

/// <summary>The Brightline web app in-memory against a real, migrated Postgres database.</summary>
public partial class AppFactory(string connectionString, Action<IServiceCollection>? configureServices = null, Dictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@brightline.test";
    public const string AdminPassword = "Admin-Password-123!";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:App", connectionString);
        builder.UseSetting("LegacyOrders:BaseUrl", "http://127.0.0.1:1");
        builder.UseSetting("LegacyOrders:ApiKey", "test-key");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Worker:Mode", "disabled");
        builder.UseSetting("Ai:Provider", "fake");
        builder.UseSetting("RateLimit:LoginPerMinute", "100");
        foreach (var (key, value) in settings ?? [])
        {
            builder.UseSetting(key, value);
        }

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }

    public async Task EnsureAdminAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>()
            .CreateUserAsync(AdminEmail, Roles.Admin, AdminPassword);
    }

    /// <summary>Signs in through the real static-SSR login form (antiforgery included) and returns a client with the cookie.</summary>
    public async Task<HttpClient> CreateSignedInClientAsync(string email = AdminEmail, string password = AdminPassword)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var page = await client.GetStringAsync("/account/login");
        var token = AntiforgeryRegex().Match(page).Groups[1].Value;
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        }));
        if ((int)response.StatusCode is < 300 or >= 400)
        {
            throw new InvalidOperationException($"Login failed: {(int)response.StatusCode}");
        }

        return client;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();
}
