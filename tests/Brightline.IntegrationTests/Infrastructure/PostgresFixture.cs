using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Brightline.IntegrationTests.Infrastructure.PostgresFixture))]

namespace Brightline.IntegrationTests.Infrastructure;

/// <summary>One real Postgres 16 + pgvector container for the whole test assembly; each test class gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg16")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates a fresh, empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"; // < 63 chars (Postgres identifier limit)
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();

        // Generous timeouts: Docker Desktop port forwarding can be slow when many tests open connections at once.
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Timeout = 60, CommandTimeout = 60 }.ConnectionString;
    }
}
