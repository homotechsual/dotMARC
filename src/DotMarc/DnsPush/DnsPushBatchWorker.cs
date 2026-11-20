using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotMarc.DnsPush;

/// <summary>Takes signed-in batches off the queue and pushes them one at a time, and keeps the stored batches tidy: a batch that was Running
/// when dotMARC stopped is marked Interrupted at startup (its provider session was lost with the process), a Planned batch nobody signed in for
/// is marked Expired, and finished batches are deleted after a week.</summary>
public sealed class DnsPushBatchWorker(
    DnsPushBatchQueue queue,
    IServiceScopeFactory scopeFactory,
    IEnumerable<IDnsPushProvider> providers,
    IDbContextFactory<DotMarcDbContext> dbFactory,
    ILogger<DnsPushBatchWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan CleanUpEvery = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken).ConfigureAwait(false);
            await CleanUpAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Tidying up stored DNS push batches at startup failed");
        }

        var cleanUp = Task.Run(() => CleanUpPeriodicallyAsync(stoppingToken), CancellationToken.None);

        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessAsync(work, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: a batch being pushed was abandoned by its runner.
        }

        await cleanUp.ConfigureAwait(false);
    }

    /// <summary>At startup: a batch left Running has lost its session, so it is Interrupted and its unfinished domains marked not attempted.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var running = await context.DnsPushBatches.Where(batch => batch.Status == DnsPushBatchStatus.Running).Select(batch => batch.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batchId in running)
        {
            await DnsPushBatchGate.AbandonAsync(context, batchId, now, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Expires a Planned batch past its window, and deletes finished batches (with their rows) older than the retention.</summary>
    public async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        await context.DnsPushBatches.Where(batch => batch.Status == DnsPushBatchStatus.Planned && batch.ExpiresUtc <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(batch => batch.Status, DnsPushBatchStatus.Expired), cancellationToken).ConfigureAwait(false);

        var cutoff = now - DnsPushBatchLimits.Retention;
        var finished = new[] { DnsPushBatchStatus.Completed, DnsPushBatchStatus.Interrupted, DnsPushBatchStatus.Expired };
        var old = await context.DnsPushBatches
            .Where(batch => finished.Contains(batch.Status) && (batch.CompletedUtc ?? batch.ExpiresUtc) < cutoff)
            .Select(batch => batch.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (old.Count == 0)
        {
            return;
        }

        await context.DnsPushBatchDomains.Where(row => old.Contains(row.BatchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.DnsPushBatches.Where(batch => old.Contains(batch.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pushes one signed-in batch. Never throws for a failing batch, so one can't stop the next: it logs without the session, then
    /// gives the batch up so it isn't left Running.</summary>
    public async Task ProcessAsync(DnsPushBatchWork work, CancellationToken cancellationToken)
    {
        try
        {
            var provider = providers.FirstOrDefault(candidate => string.Equals(candidate.ProviderKey, work.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                await AbandonAsync(work.BatchId).ConfigureAwait(false);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<DnsPushBatchRunner>();
            await runner.RunAsync(work.BatchId, work.Session, provider, work.Actor, DnsPushBatchLimits.PauseBetweenDomains, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The exception type and batch id only: the exception message and the work record (which holds the session) stay out of the log.
            logger.LogError("A DNS push batch {BatchId} failed unexpectedly ({ExceptionType})", work.BatchId, exception.GetType().Name);
            try
            {
                await AbandonAsync(work.BatchId).ConfigureAwait(false);
            }
            catch (Exception abandonFailure)
            {
                logger.LogError("Giving up DNS push batch {BatchId} failed ({ExceptionType})", work.BatchId, abandonFailure.GetType().Name);
            }
        }
    }

    private async Task AbandonAsync(Guid batchId)
    {
        await using var context = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        await DnsPushBatchGate.AbandonAsync(context, batchId, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CleanUpPeriodicallyAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanUpEvery, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await CleanUpAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Tidying up stored DNS push batches failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
