using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Pushes a reviewed batch, one domain at a time, with the one provider session its person signed in with. Each domain's plan is
/// worked out again and compared with the fingerprint that was reviewed, so a domain whose DNS changed in between is skipped and reported,
/// never pushed. One domain failing doesn't stop the rest. Nothing here keeps or logs the session or a provider exception's message.</summary>
public sealed class DnsPushBatchRunner(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    DnsPushPlanner planner,
    IEnumerable<IDnsChangeBuilder> changeBuilders,
    AuditRecorder auditRecorder,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const int MaxDetailLength = 500;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((pause, token) => Task.Delay(pause, token));

    /// <summary>Runs a batch that has been claimed (is Running). Anything else is left alone. If it is cancelled, or can't go on, the batch is
    /// Interrupted and the domains not yet started are marked not attempted.</summary>
    public async Task RunAsync(Guid batchId, IDnsPushSession session, IDnsPushProvider provider, AuditActor actor, TimeSpan pauseBetweenDomains, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        var batch = await context.DnsPushBatches.Include(candidate => candidate.Domains)
            .SingleOrDefaultAsync(candidate => candidate.Id == batchId, CancellationToken.None).ConfigureAwait(false);

        if (batch is not { Status: DnsPushBatchStatus.Running })
        {
            return;
        }

        var builder = changeBuilders.Find(batch.Target);
        var rows = batch.Domains.Where(row => row.Included && row.Status == DnsPushBatchDomainStatus.Planned).OrderBy(row => row.Position).ToList();

        try
        {
            if (builder is null)
            {
                await DnsPushBatchGate.AbandonAsync(context, batchId, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            for (var index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await PushOneAsync(context, batch, rows[index], builder, session, provider, actor, cancellationToken).ConfigureAwait(false);

                if (index < rows.Count - 1 && pauseBetweenDomains > TimeSpan.Zero)
                {
                    await _delay(pauseBetweenDomains, cancellationToken).ConfigureAwait(false);
                }
            }

            batch.Status = DnsPushBatchStatus.Completed;
            batch.CompletedUtc = _time.GetUtcNow();
            await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.ChangeTracker.Clear();
            await DnsPushBatchGate.AbandonAsync(context, batchId, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PushOneAsync(
        DotMarcDbContext context, DnsPushBatch batch, DnsPushBatchDomain row, IDnsChangeBuilder builder,
        IDnsPushSession session, IDnsPushProvider provider, AuditActor actor, CancellationToken cancellationToken)
    {
        var domain = await context.Domains.AsNoTracking().Include(candidate => candidate.DkimRecords)
            .SingleOrDefaultAsync(candidate => candidate.Id == row.DomainId, cancellationToken).ConfigureAwait(false);

        if (domain is null)
        {
            await FinishAsync(context, row, DnsPushBatchDomainStatus.Failed, "not-found").ConfigureAwait(false);
            return;
        }

        var planning = await planner.PlanAsync(domain, builder, batch.Provider, payload: null, cancellationToken).ConfigureAwait(false);
        if (planning.Plan is null)
        {
            var status = planning.Flag == "zone-not-found" ? DnsPushBatchDomainStatus.ZoneNotFound : DnsPushBatchDomainStatus.Failed;
            await FinishAsync(context, row, status, planning.Flag).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(DnsPlanFingerprint.Of(planning.Plan.Changes), row.PlanFingerprint, StringComparison.Ordinal))
        {
            await FinishAsync(context, row, DnsPushBatchDomainStatus.ChangedSinceReview, null).ConfigureAwait(false);
            return;
        }

        DnsPushResult result;
        try
        {
            result = await provider.PushAsync(session, planning.Plan.Changes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped in the middle of a push: the zone may or may not have changed, so this one is not "not attempted".
            await FinishAsync(context, row, DnsPushBatchDomainStatus.Failed, "Stopped during the push, so check this domain's DNS.").ConfigureAwait(false);
            await AuditAsync(actor, domain, batch.Provider, planning.Plan.Changes, DnsPushOutcome.ProviderError).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            // The message is dropped on purpose: it isn't known to be free of anything sensitive.
            await FinishAsync(context, row, DnsPushBatchDomainStatus.Failed, "error").ConfigureAwait(false);
            await AuditAsync(actor, domain, batch.Provider, planning.Plan.Changes, DnsPushOutcome.ProviderError).ConfigureAwait(false);
            return;
        }

        var rowStatus = result.Outcome switch
        {
            DnsPushOutcome.Pushed => DnsPushBatchDomainStatus.Pushed,
            DnsPushOutcome.ZoneNotFound => DnsPushBatchDomainStatus.ZoneNotFound,
            DnsPushOutcome.ReplaceFailedAfterDelete or DnsPushOutcome.LeftDuplicateRecord => DnsPushBatchDomainStatus.FailedAfterChange,
            _ => DnsPushBatchDomainStatus.Failed,
        };

        await FinishAsync(context, row, rowStatus, rowStatus == DnsPushBatchDomainStatus.Pushed ? null : result.DetailMessage).ConfigureAwait(false);
        await AuditAsync(actor, domain, batch.Provider, planning.Plan.Changes, result.Outcome).ConfigureAwait(false);
    }

    private async Task FinishAsync(DotMarcDbContext context, DnsPushBatchDomain row, DnsPushBatchDomainStatus status, string? detail)
    {
        row.Status = status;
        row.Detail = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;
        row.FinishedUtc = _time.GetUtcNow();
        await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task AuditAsync(AuditActor actor, Domain domain, string provider, IReadOnlyList<DnsRecordChange> changes, DnsPushOutcome outcome)
    {
        if (DnsPushAudit.CreateEntry(actor, domain, provider, changes, outcome) is { } entry)
        {
            await auditRecorder.RecordAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
