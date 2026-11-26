using Brightline.Data.Jobs;
using Brightline.Knowledge.Ingestion;
using Brightline.Knowledge.Search;
using CommunityToolkit.VectorData.PgVector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Npgsql;

namespace Brightline.Knowledge;

public static class KnowledgeServiceCollectionExtensions
{
    public const string ChunksTable = "chunks";

    public static IServiceCollection AddBrightlineKnowledge(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KnowledgeOptions>(configuration.GetSection(KnowledgeOptions.Section));

        var connectionString = configuration.GetConnectionString("App")
            ?? throw new InvalidOperationException("ConnectionStrings:App is required.");
        services.AddKeyedSingleton<NpgsqlDataSource>("vector", (_, _) =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector();
            return builder.Build();
        });

        // The collection maps onto the EF-migrated "chunks" table; it never creates or alters schema.
        services.AddSingleton<VectorStoreCollection<Guid, ChunkRecord>>(sp => new PostgresCollection<Guid, ChunkRecord>(
            sp.GetRequiredKeyedService<NpgsqlDataSource>("vector"), ChunksTable, ownsDataSource: false, new PostgresCollectionOptions()));

        services.AddScoped<KnowledgeIngestor>();
        services.AddScoped<KnowledgeService>();
        services.AddScoped<IKnowledgeSearch, HybridSearch>();
        services.AddScoped<IJobHandler, IngestionJobHandler>();
        return services;
    }
}
