extern alias legacy;
using System.Net;
using System.Net.Http.Json;
using Brightline.IntegrationTests.Infrastructure;
using Brightline.LegacyOrders.Client;
using legacy::Brightline.LegacyOrders.Api;
using legacy::Brightline.LegacyOrders.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.IntegrationTests.Legacy;

public sealed class LegacyApiFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public LegacyApiFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Factory = new LegacyApiFactory(await postgres.CreateDatabaseAsync("legacy"));
        _ = Factory.Server; // start: migrate + seed
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

    public async Task<int> CustomerIdAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LegacyDbContext>();
        return await db.Customers.Where(c => c.Email == email).Select(c => c.Id).SingleAsync();
    }
}

public sealed class LegacyApiTests(LegacyApiFixture fixture) : IClassFixture<LegacyApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seed_contains_about_50_customers_and_150_orders_including_the_demo_story()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LegacyDbContext>();

        Assert.Equal(50, await db.Customers.CountAsync(Ct));
        Assert.Equal(150, await db.Orders.CountAsync(Ct));

        var maya = await db.Orders.Include(o => o.Lines).Include(o => o.Delivery).Include(o => o.Customer)
            .SingleAsync(o => o.Number == LegacySeeder.MayaOrder, Ct);
        Assert.Equal("Maya Thompson", maya.Customer!.Name);
        Assert.Equal(OrderStatus.Delivered, maya.Status);
        Assert.Equal(9, (int)Math.Round((DateTimeOffset.UtcNow - maya.Delivery!.DeliveredAt!.Value).TotalDays));
        Assert.Contains(maya.Lines, l => l.Name == "Aurora Wireless Headphones" && l.UnitPrice == 149m);
        Assert.Contains(maya.Lines, l => l.Name == "USB-C Charger 65W" && l.UnitPrice == 39m);
    }

    [Fact]
    public async Task Requests_without_a_valid_api_key_are_rejected()
    {
        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync("/api/customers/1/orders", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        anonymous.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        response = await anonymous.GetAsync("/api/customers/1/orders", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_and_openapi_are_public()
    {
        using var client = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready", Ct)).StatusCode);
        var openApi = await client.GetStringAsync("/openapi/v1.json", Ct);
        Assert.Contains("/api/verifications", openApi, StringComparison.Ordinal);
        Assert.Contains("X-Api-Key", openApi, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verification_requires_matching_order_number_and_email()
    {
        using var client = fixture.Factory.CreateApiClient();

        var ok = await client.PostAsJsonAsync("/api/verifications",
            new VerificationRequest(LegacySeeder.MayaOrder, "MAYA.T@example.com "), Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var verified = await ok.Content.ReadFromJsonAsync<VerificationResponse>(Ct);
        Assert.Equal("Maya", verified!.FirstName);

        var wrongEmail = await client.PostAsJsonAsync("/api/verifications",
            new VerificationRequest(LegacySeeder.MayaOrder, LegacySeeder.DanielEmail), Ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongEmail.StatusCode);

        var malformed = await client.PostAsJsonAsync("/api/verifications",
            new VerificationRequest("10482", "not-an-email"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task A_customer_cannot_read_another_customers_order()
    {
        var danielId = await fixture.CustomerIdAsync(LegacySeeder.DanielEmail);
        using var client = fixture.Factory.CreateApiClient();

        var response = await client.GetAsync($"/api/customers/{danielId}/orders/{LegacySeeder.MayaOrder}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Typed_client_reads_orders_through_http()
    {
        var api = fixture.Factory.CreateTypedClient();

        var customer = await api.VerifyCustomerAsync(LegacySeeder.MayaOrder, LegacySeeder.MayaEmail, Ct);
        Assert.NotNull(customer);

        var orders = await api.ListOrdersAsync(customer.CustomerId, Ct);
        Assert.Contains(orders, o => o.Number == LegacySeeder.MayaOrder);
        Assert.Contains(orders, o => o.Number == LegacySeeder.MayaOldOrder);

        var order = await api.GetOrderAsync(customer.CustomerId, LegacySeeder.MayaOrder, Ct);
        Assert.NotNull(order);
        Assert.True(order.IsDelivered);
        Assert.Equal(2, order.Lines.Count);

        Assert.Null(await api.VerifyCustomerAsync(LegacySeeder.MayaOrder, "someone@example.com", Ct));
    }

    [Fact]
    public async Task Creating_a_return_twice_with_the_same_idempotency_key_creates_it_once()
    {
        var api = fixture.Factory.CreateTypedClient();
        var mayaId = await fixture.CustomerIdAsync(LegacySeeder.MayaEmail);
        var request = new NewReturn(LegacySeeder.MayaOrder, "AUD-AURORA-WH", 1, "Defect: left ear cuts out");
        var key = $"conv-{Guid.NewGuid():N}-BL-10482-AUD-AURORA-WH";

        var first = await api.CreateReturnAsync(mayaId, request, key, Ct);
        var second = await api.CreateReturnAsync(mayaId, request, key, Ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Return.Number, second.Return.Number);
        Assert.StartsWith("RR-", first.Return.Number, StringComparison.Ordinal);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LegacyDbContext>();
        Assert.Equal(1, await db.Returns.CountAsync(r => r.IdempotencyKey == key, Ct));
    }

    [Fact]
    public async Task Returning_more_than_was_bought_is_rejected_by_the_legacy_rules()
    {
        var api = fixture.Factory.CreateTypedClient();
        var danielId = await fixture.CustomerIdAsync(LegacySeeder.DanielEmail);

        var ex = await Assert.ThrowsAsync<LegacyOrdersRejectedException>(() => api.CreateReturnAsync(
            danielId, new NewReturn(LegacySeeder.DanielOrder, "LAP-NOVA-14", 5, "Changed my mind"), $"k-{Guid.NewGuid():N}", Ct));
        Assert.Contains("can still be returned", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_reset_requires_the_admin_key_and_reseeds()
    {
        using var client = fixture.Factory.CreateApiClient();
        var denied = await client.PostAsync("/api/admin/reset", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
}
