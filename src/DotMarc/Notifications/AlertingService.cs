using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

public interface IAlertingService
{
    Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default);
    Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default);
}

public sealed class AlertingService : IAlertingService
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IAlertWebhookClient _alertWebhookClient;
    private readonly ILogger<AlertingService> _logger;

    public AlertingService(IDbContextFactory<DotMarcDbContext> dbFactory, IAlertWebhookClient alertWebhookClient, ILogger<AlertingService> logger)
    {
        _dbFactory = dbFactory;
        _alertWebhookClient = alertWebhookClient;
        _logger = logger;
    }

    public async Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Read live rather than once at startup: this is a singleton service, and settings are
        // now editable at any time from /alerts/settings (see NotificationSettings's doc
        // comment) — a value bound once via IOptions would never pick up a later change without
        // a restart.
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-settings.MissingReportThresholdDays);
        var domains = await db.Domains
            .AsNoTracking()
            .Where(d => d.IsMonitored)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var domain in domains)
        {
            if (domain.LastReportReceivedUtc is { } lastReport && lastReport >= cutoffUtc)
            {
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var message = domain.LastReportReceivedUtc is { } receivedUtc
                ? $"The monitored domain '{domain.Name}' has not received a DMARC report since {receivedUtc:O}."
                : $"The monitored domain '{domain.Name}' has not received a DMARC report yet.";
            await EnsureAlertAsync(db, settings, domain.Name, "MissedReport", "Warning", "Missing expected DMARC report", message, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var activeAlert = await db.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == "MissedReport" && !e.IsResolved)
            .OrderByDescending(e => e.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeAlert is null)
        {
            return;
        }

        activeAlert.IsResolved = true;
        activeAlert.ResolvedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAlertAsync(DotMarcDbContext context, NotificationSettings settings, string domainName, string alertType, string severity, string title, string message, CancellationToken cancellationToken)
    {
        var activeAlert = await context.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == alertType && !e.IsResolved)
            .OrderByDescending(e => e.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeAlert is not null)
        {
            var cooldown = TimeSpan.FromMinutes(settings.CooldownMinutes);
            if (activeAlert.CreatedUtc > DateTimeOffset.UtcNow.Subtract(cooldown))
            {
                return;
            }
        }

        var alert = new AlertEvent
        {
            DomainName = domainName,
            AlertType = alertType,
            Severity = severity,
            Title = title,
            Message = message,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _alertWebhookClient.SendAlertAsync(settings, domainName, alertType, title, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for {DomainName} alert {AlertType}.", domainName, alertType);
        }
    }
}
