using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotMarc.Notifications;

public sealed class PinnedDomainHealthMonitor : BackgroundService
{
    private readonly IAlertingService _alertingService;
    private readonly NotificationOptions _options;
    private readonly ILogger<PinnedDomainHealthMonitor> _logger;

    public PinnedDomainHealthMonitor(IAlertingService alertingService, IOptions<NotificationOptions> options, ILogger<PinnedDomainHealthMonitor> logger)
    {
        _alertingService = alertingService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.Enabled)
            {
                try
                {
                    await _alertingService.CheckPinnedDomainsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pinned domain health check failed; retrying on next interval.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.MonitorIntervalSeconds), stoppingToken).ConfigureAwait(false);
        }
    }
}
