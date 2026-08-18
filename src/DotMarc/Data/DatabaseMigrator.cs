using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Runs EF Core migrations guarded by a Postgres advisory lock, so multiple replicas
/// starting concurrently (rolling deploy, scale-out) don't race to apply migrations against the
/// shared database. Every replica still calls this at startup: the lock only serializes them —
/// whichever one goes first actually applies pending migrations, and the rest, having waited
/// for the lock, then call MigrateAsync too and find nothing left to do.</summary>
public static class DatabaseMigrator
{
    /// <summary>Arbitrary fixed key for this lock — distinct from
    /// <see cref="Ingestion.PollingService.PollingLeaderLockKey"/> so the two don't collide.</summary>
    internal const long MigrationLeaderLockKey = 84_200_002;

    public static async Task MigrateWithLeaderLockAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", MigrationLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
