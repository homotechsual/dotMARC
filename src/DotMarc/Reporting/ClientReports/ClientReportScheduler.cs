namespace DotMarc.Reporting.ClientReports;

/// <summary>Runs the dispatcher every 15 minutes, starting a minute after startup.</summary>
public sealed class ClientReportScheduler(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<ClientReportScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ClientReportDispatcher>().RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Checking for client reports to send failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
