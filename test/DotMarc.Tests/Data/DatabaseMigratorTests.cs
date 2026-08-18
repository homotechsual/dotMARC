using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Data;

/// <summary>Covers DatabaseMigrator.MigrateWithLeaderLockAsync's Postgres advisory-lock guard:
/// multiple replicas starting concurrently (rolling deploy, scale-out) must not race to apply
/// migrations against the same database — each waits its turn for the lock, then runs
/// MigrateAsync (a no-op once a prior holder already applied everything).</summary>
[Collection("Postgres")]
public sealed class DatabaseMigratorTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DatabaseMigratorTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task MigrateWithLeaderLockAsync_AppliesPendingMigrations()
    {
        using var context = CreateContext();

        await DatabaseMigrator.MigrateWithLeaderLockAsync(context);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task MigrateWithLeaderLockAsync_WaitsForAnotherHolderToReleaseTheLock_ThenMigrates()
    {
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", DatabaseMigrator.MigrationLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        using var context = CreateContext();
        var migrateTask = DatabaseMigrator.MigrateWithLeaderLockAsync(context);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(migrateTask.IsCompleted);

        await lockTransaction.CommitAsync();

        await migrateTask;
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }
}
