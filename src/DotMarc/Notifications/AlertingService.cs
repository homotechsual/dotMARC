using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

public interface IAlertingService
{
    Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default);
    Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default);
    Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default);
    Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default);
}

public sealed class AlertingService : IAlertingService
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IAlertWebhookClient _alertWebhookClient;
    private readonly IPsaTicketService _psaTicketService;
    private readonly ILogger<AlertingService> _logger;

    public AlertingService(IDbContextFactory<DotMarcDbContext> dbFactory, IAlertWebhookClient alertWebhookClient, IPsaTicketService psaTicketService, ILogger<AlertingService> logger)
    {
        _dbFactory = dbFactory;
        _alertWebhookClient = alertWebhookClient;
        _psaTicketService = psaTicketService;
        _logger = logger;
    }

    public async Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Read live rather than once at startup: this is a singleton service, and settings are
        // now editable at any time from /alerts/settings (see NotificationSettings's doc
        // comment) - a value bound once via IOptions would never pick up a later change without
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
            if (domain.SpfCheckStatus == SpfCheckStatus.NullSpf)
            {
                // Null-routed (SPF v=spf1 -all): no reports is the expected, healthy state, not a
                // problem - resolve any pre-existing alert from before the domain became
                // null-routed and skip the missing-report check entirely for it.
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);

                // UnexpectedActivityOnNullRoutedDomain resolves only once the domain has gone
                // quiet again (no report within the missing-report threshold window). This branch
                // runs unconditionally for every null-routed domain each cycle, so resolving it
                // unconditionally here would auto-close the alert on the very next poll after it
                // fires, defeating its purpose of surfacing unexpected activity for an admin to
                // see. Same threshold semantics as MissedReport, just inverted: fires when a
                // report unexpectedly arrives, stays open while reports keep arriving within the
                // threshold window, auto-resolves once activity stops for the threshold period.
                if (domain.LastReportReceivedUtc is null || domain.LastReportReceivedUtc < cutoffUtc)
                {
                    await ResolveAlertAsync(domain.Name, "UnexpectedActivityOnNullRoutedDomain", cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

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
        => await ResolveAlertAsync(domainName, "MissedReport", cancellationToken).ConfigureAwait(false);

    public async Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default)
    {
        if (failedSessionCount == 0)
        {
            await ResolveAlertAsync(domainName, "TlsrptFailure", cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var failureSummary = failureTypes.Count == 0 ? "no failure category supplied" : string.Join(", ", failureTypes.Distinct(StringComparer.OrdinalIgnoreCase));
        await EnsureAlertAsync(db, settings, domainName, "TlsrptFailure", "Warning", "TLS delivery failures reported", $"TLSRPT reported {failedSessionCount} failed TLS delivery session(s) for '{domainName}'. Failure types: {failureSummary}.", cancellationToken).ConfigureAwait(false);
    }

    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var message = $"'{domainName}' is marked null-routed (SPF v=spf1 -all - no authorized senders) but a DMARC aggregate report just arrived showing mail activity. This may be legitimate traffic that needs accounting for, or a spoofing attempt.";
        await EnsureAlertAsync(db, settings, domainName, "UnexpectedActivityOnNullRoutedDomain", "Warning", "Unexpected mail activity on a null-routed domain", message, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveAlertAsync(string domainName, string alertType, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var activeAlert = await db.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == alertType && !e.IsResolved)
            .OrderByDescending(e => e.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeAlert is null)
        {
            return;
        }

        activeAlert.IsResolved = true;
        activeAlert.ResolvedUtc = DateTimeOffset.UtcNow;

        try
        {
            await _psaTicketService.CloseTicketAsync(db, activeAlert, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close PSA ticket for {DomainName} alert {AlertType}.", activeAlert.DomainName, activeAlert.AlertType);
        }

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

        try
        {
            await _psaTicketService.CreateTicketAsync(context, alert, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create PSA ticket for {DomainName} alert {AlertType}.", domainName, alertType);
        }
    }
}
