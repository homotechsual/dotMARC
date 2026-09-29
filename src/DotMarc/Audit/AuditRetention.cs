using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Audit;

/// <summary>Deletes entries past their kind's retention period.</summary>
public static class AuditRetention
{
    /// <summary>Deleted a batch at a time so the first cleanup of a large log doesn't hold one long transaction.</summary>
    public const int BatchSize = 5000;

    public static async Task<int> PurgeAsync(DotMarcDbContext context, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var settings = await context.AuditSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        deleted += await PurgeKindAsync(context, AuditEntryKind.Change, settings.ChangeRetentionDays, nowUtc, cancellationToken).ConfigureAwait(false);
        deleted += await PurgeKindAsync(context, AuditEntryKind.SignIn, settings.SignInRetentionDays, nowUtc, cancellationToken).ConfigureAwait(false);
        deleted += await PurgeKindAsync(context, AuditEntryKind.PageView, settings.PageViewRetentionDays, nowUtc, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static async Task<int> PurgeKindAsync(DotMarcDbContext context, AuditEntryKind kind, int? retentionDays, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (retentionDays is not { } days)
        {
            return 0;
        }

        var cutoff = nowUtc.AddDays(-days);
        var deleted = 0;
        while (true)
        {
            var expiredIds = await context.AuditEntries
                .Where(entry => entry.Kind == kind && entry.OccurredUtc < cutoff)
                .OrderBy(entry => entry.Id)
                .Select(entry => entry.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (expiredIds.Count == 0)
            {
                return deleted;
            }

            deleted += await context.AuditEntries.Where(entry => expiredIds.Contains(entry.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
