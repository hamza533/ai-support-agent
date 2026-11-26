extern alias legacy;

using Brightline.LegacyOrders.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.IntegrationTests.Infrastructure;

/// <summary>Runs the legacy Orders API in-memory against a real Postgres database (migrated + seeded on start).</summary>
public sealed class LegacyApiFactory(string connectionString) : WebApplicationFactory<legacy::Brightline.LegacyOrders.Program>
{
    public const string ApiKey = "test-api-key";
    public const string AdminKey = "test-admin-key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:LegacyOrders", connectionString);
        builder.UseSetting("LegacyOrders:ApiKey", ApiKey);
        builder.UseSetting("LegacyOrders:AdminKey", AdminKey);
        builder.UseSetting("LegacyOrders:MigrateOnStartup", "true");
        builder.UseSetting("LegacyOrders:SeedOnStartup", "true");
    }

    public HttpClient CreateApiClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    /// <summary>The app's real typed client (with its resilience pipeline) wired to this in-memory server.</summary>
    public ILegacyOrdersClient CreateTypedClient()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegacyOrders:BaseUrl"] = "http://legacy.test",
                ["LegacyOrders:ApiKey"] = ApiKey,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLegacyOrdersClient(config)
            .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        return services.BuildServiceProvider().GetRequiredService<ILegacyOrdersClient>();
    }
}
