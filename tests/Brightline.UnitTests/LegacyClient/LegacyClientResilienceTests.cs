using System.Net;
using Brightline.LegacyOrders.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.UnitTests.LegacyClient;

/// <summary>With the legacy API down, the client retries, then breaks the circuit, and surfaces "unavailable".</summary>
public sealed class LegacyClientResilienceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Server_errors_are_retried_then_reported_as_unavailable()
    {
        var handler = new CountingHandler(HttpStatusCode.ServiceUnavailable);
        var client = Build(handler, baseUrl: "http://legacy.test");

        await Assert.ThrowsAsync<LegacyOrdersUnavailableException>(() => client.ListOrdersAsync(1, Ct));

        Assert.Equal(4, handler.Calls); // 1 attempt + 3 retries
    }

    [Fact]
    public async Task Repeated_failures_open_the_circuit_so_calls_fail_fast()
    {
        var handler = new CountingHandler(HttpStatusCode.InternalServerError);
        var client = Build(handler, baseUrl: "http://legacy.test");

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<LegacyOrdersUnavailableException>(() => client.ListOrdersAsync(1, Ct));
        }

        var callsBefore = handler.Calls;
        var ex = await Assert.ThrowsAsync<LegacyOrdersUnavailableException>(() => client.ListOrdersAsync(1, Ct));

        Assert.Contains("circuit open", ex.Message, StringComparison.Ordinal);
        Assert.Equal(callsBefore, handler.Calls);
    }

    [Fact]
    public async Task Unreachable_host_is_reported_as_unavailable()
    {
        var services = Services();
        services.AddLegacyOrdersClient(Config("http://127.0.0.1:1"));
        var client = services.BuildServiceProvider().GetRequiredService<ILegacyOrdersClient>();

        await Assert.ThrowsAsync<LegacyOrdersUnavailableException>(() => client.VerifyCustomerAsync("BL-10482", "maya.t@example.com", Ct));
    }

    [Fact]
    public async Task Create_return_sends_the_idempotency_key_on_every_attempt()
    {
        var handler = new CountingHandler(HttpStatusCode.BadGateway);
        var client = Build(handler, baseUrl: "http://legacy.test");

        await Assert.ThrowsAsync<LegacyOrdersUnavailableException>(() =>
            client.CreateReturnAsync(1, new NewReturn("BL-10482", "AUD-AURORA-WH", 1, "defect"), "key-123", Ct));

        Assert.Equal(4, handler.Calls);
        Assert.All(handler.IdempotencyKeys, k => Assert.Equal("key-123", k));
    }

    private static ILegacyOrdersClient Build(HttpMessageHandler handler, string baseUrl)
    {
        var services = Services();
        services.AddLegacyOrdersClient(Config(baseUrl)).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider().GetRequiredService<ILegacyOrdersClient>();
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static IConfiguration Config(string baseUrl) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegacyOrders:BaseUrl"] = baseUrl,
            ["LegacyOrders:ApiKey"] = "k",
            ["LegacyOrders:Resilience:Retry:Delay"] = "00:00:00.001",
            ["LegacyOrders:Resilience:Retry:UseJitter"] = "false",
            ["LegacyOrders:Resilience:CircuitBreaker:MinimumThroughput"] = "8",
            ["LegacyOrders:Resilience:AttemptTimeout:Timeout"] = "00:00:02",
        })
        .Build();

    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        public List<string> IdempotencyKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (request.Headers.TryGetValues(LegacyOrdersClient.IdempotencyHeader, out var keys))
            {
                lock (IdempotencyKeys)
                {
                    IdempotencyKeys.AddRange(keys);
                }
            }

            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
