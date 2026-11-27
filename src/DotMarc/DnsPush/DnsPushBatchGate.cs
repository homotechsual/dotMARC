using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Why a batch can't be signed in to. Ordered the way the checks run.</summary>
public enum DnsPushBatchProblem { NotFound, SomeoneElses, WrongProvider, NotPlanned, Expired, NothingIncluded }

/// <summary>The batch when it can be used, or the reason it can't. Exactly one of the two is set.</summary>
public sealed record DnsPushBatchCheck(DnsPushBatch? Batch, DnsPushBatchProblem? Problem);

/// <summary>The checks around signing in to run a batch: that it exists, is this person's, is for this provider, is still waiting and in
/// time and has something to push, and the claim that makes a batch usable once.</summary>
public static class DnsPushBatchGate
{
    public static async Task<DnsPushBatchCheck> CheckAsync(
        DotMarcDbContext context, Guid batchId, string? personKey, string provider, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var batch = await context.DnsPushBatches.AsNoTracking().Include(candidate => candidate.Domains)
            .SingleOrDefaultAsync(candidate => candidate.Id == batchId, cancellationToken).ConfigureAwait(false);

        if (batch is null)
        {
            return new DnsPushBatchCheck(null, DnsPushBatchProblem.NotFound);
        }

        // A person only: an API key has no person key, so it can never match.
        if (personKey is null || !string.Equals(personKey, batch.StartedBy, StringComparison.Ordinal))
        {
            return new DnsPushBatchCheck(null, DnsPushBatchProblem.SomeoneElses);
        }

        if (!string.Equals(provider, batch.Provider, StringComparison.OrdinalIgnoreCase))
        {
            return new DnsPushBatchCheck(null, DnsPushBatchProblem.WrongProvider);
        }

        if (batch.Status != DnsPushBatchStatus.Planned)
        {
            return new DnsPushBatchCheck(null, DnsPushBatchProblem.NotPlanned);
        }

        if (batch.ExpiresUtc <= nowUtc)
        {
            return new DnsPushBatchCheck(null, DnsPushBatchProblem.Expired);
        }

        return batch.Domains.Any(row => row.Included)
            ? new DnsPushBatchCheck(batch, null)
            : new DnsPushBatchCheck(null, DnsPushBatchProblem.NothingIncluded);
    }

    /// <summary>Marks a batch Running, but only if it is still Planned and inside its window, in one statement, so of any number of callers
    /// at once exactly one wins. True for the winner.</summary>
    public static async Task<bool> ClaimAsync(DotMarcDbContext context, Guid batchId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        await context.DnsPushBatches
            .Where(batch => batch.Id == batchId && batch.Status == DnsPushBatchStatus.Planned && batch.ExpiresUtc > nowUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(batch => batch.Status, DnsPushBatchStatus.Running)
                .SetProperty(batch => batch.StartedUtc, nowUtc), cancellationToken).ConfigureAwait(false) == 1;

    /// <summary>Gives up a claimed batch that can't go on (the provider rejected the sign-in, or dotMARC stopped): Interrupted, with the domains
    /// that were still to be pushed marked as not attempted.</summary>
    public static async Task AbandonAsync(DotMarcDbContext context, Guid batchId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await context.DnsPushBatchDomains
            .Where(row => row.BatchId == batchId && row.Included && row.Status == DnsPushBatchDomainStatus.Planned)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, DnsPushBatchDomainStatus.NotAttempted), cancellationToken).ConfigureAwait(false);

        await context.DnsPushBatches
            .Where(batch => batch.Id == batchId && batch.Status == DnsPushBatchStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(batch => batch.Status, DnsPushBatchStatus.Interrupted)
                .SetProperty(batch => batch.CompletedUtc, nowUtc), cancellationToken).ConfigureAwait(false);
    }
}
