using System.Net;
using System.Text.Json;
using Brightline.Data;
using Brightline.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.IntegrationTests.App;

public sealed class AppFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public AppFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Factory = new AppFactory(await postgres.CreateDatabaseAsync("app"));
        _ = Factory.Server;
        await Factory.EnsureAdminAsync();
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class AppHostTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_create_the_schema_including_vector_and_full_text_columns()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        var columns = await db.Database.SqlQueryRaw<string>(
            "SELECT column_name || ':' || udt_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'chunks'").ToListAsync(Ct);
        Assert.Contains("embedding:vector", columns);
        Assert.Contains("tsv:tsvector", columns);
    }

    [Fact]
    public async Task Health_is_live_and_ready_reports_dependencies()
    {
        using var client = fixture.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);

        var ready = await client.GetAsync("/ready", Ct);
        using var json = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));
        var checks = json.RootElement.GetProperty("checks");
        Assert.Equal("Healthy", checks.GetProperty("database").GetString());
        Assert.Equal("Degraded", checks.GetProperty("legacy-orders-api").GetString()); // legacy API not running here
    }

    [Fact]
    public async Task Pages_send_security_headers()
    {
        using var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Admin_requires_sign_in()
    {
        using var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/admin", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staff_can_sign_in_and_reach_admin_and_keys_are_persisted_in_the_database()
    {
        using var client = await fixture.Factory.CreateSignedInClientAsync();

        var admin = await client.GetAsync("/admin", Ct);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Contains("Overview", await admin.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.DataProtectionKeys.AnyAsync(Ct));
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Factory.CreateSignedInClientAsync(AppFactory.AdminEmail, "wrong-password-xyz"));
    }
}
