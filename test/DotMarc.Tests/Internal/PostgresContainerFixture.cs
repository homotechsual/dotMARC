using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DotMarc.Tests.Internal;

/// <summary>One Postgres container shared across the whole test run (starting it is the expensive
/// part — several seconds), with each test getting its own freshly-created, freshly-migrated
/// database on that shared container (cheap — a CREATE DATABASE against an already-running server).
/// This matches the isolation the project's previous per-test temp-file SQLite database gave,
/// without paying container startup cost per test. Verified during planning: container start,
/// connect, create/connect-to/query a fresh database, drop it, and dispose the container all
/// complete successfully against a real postgres:18 image.</summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<(string ConnectionString, IAsyncDisposable Cleanup)> CreateDatabaseAsync()
    {
        var databaseName = $"test_{Guid.NewGuid():N}";
        var adminConnectionString = _container.GetConnectionString();

        await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", adminConnection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName };
        return (builder.ConnectionString, new DatabaseCleanup(adminConnectionString, databaseName));
    }

    private sealed class DatabaseCleanup(string adminConnectionString, string databaseName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();

            // Postgres refuses to drop a database with active connections — terminate any first.
            await using (var terminate = new NpgsqlCommand(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{databaseName}' AND pid <> pg_backend_pid()", connection))
            {
                await terminate.ExecuteNonQueryAsync();
            }

            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainerFixture>;
