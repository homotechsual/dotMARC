using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

public sealed class PinnedDomainHealthMonitor : BackgroundService
{
    private const long AdvisoryLockId = 829_384_120_733_591_644;
    private readonly IAlertingService _alertingService;
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly ILogger<PinnedDomainHealthMonitor> _logger;

    public PinnedDomainHealthMonitor(IAlertingService alertingService, IDbContextFactory<DotMarcDbContext> dbFactory, ILogger<PinnedDomainHealthMonitor> logger)
    {
        _alertingService = alertingService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Read live each iteration rather than binding once at startup: settings are editable
            // at any time from /alerts/settings, and MonitorIntervalSeconds itself is one of them
            // — AlertingService.CheckPinnedDomainsAsync separately re-reads Enabled/threshold/
            // cooldown, so this fetch only needs Enabled and MonitorIntervalSeconds to drive this
            // loop, but there's no cheaper way to get just those two than reading the same row.
            int monitorIntervalSeconds;
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
                var settings = await NotificationSettingsService.GetAsync(db, stoppingToken).ConfigureAwait(false);
                monitorIntervalSeconds = settings.MonitorIntervalSeconds;

                if (settings.Enabled && await TryAcquireMonitorLockAsync(db, stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        await _alertingService.CheckPinnedDomainsAsync(stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await ReleaseMonitorLockAsync(db, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pinned domain health check failed; retrying on next interval.");
                monitorIntervalSeconds = 300;
            }

            await Task.Delay(TimeSpan.FromSeconds(monitorIntervalSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> TryAcquireMonitorLockAsync(DotMarcDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@lockId)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@lockId";
        parameter.Value = AdvisoryLockId;
        command.Parameters.Add(parameter);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task ReleaseMonitorLockAsync(DotMarcDbContext context, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(@lockId)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@lockId";
        parameter.Value = AdvisoryLockId;
        command.Parameters.Add(parameter);

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
