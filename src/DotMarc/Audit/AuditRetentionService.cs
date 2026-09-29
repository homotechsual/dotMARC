using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotMarc.Audit;

/// <summary>Runs <see cref="AuditRetention.PurgeAsync"/> a few minutes after startup and then once a day. A failed
/// run is logged and tried again the next day. Safe with several instances running: they delete the same rows.</summary>
public sealed class AuditRetentionService(IDbContextFactory<DotMarcDbContext> dbContextFactory, ILogger<AuditRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task PurgeOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
            var deleted = await AuditRetention.PurgeAsync(context, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            if (deleted > 0)
            {
                logger.LogInformation("Removed {Count} audit entries past their retention period", deleted);
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Audit retention cleanup failed; it will try again tomorrow");
        }
    }
}
