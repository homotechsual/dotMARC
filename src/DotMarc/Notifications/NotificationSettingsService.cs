using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Read/update the singleton NotificationSettings row. Follows this project's
/// DomainManagementService convention of a static class operating on a caller-supplied
/// DotMarcDbContext. SingleAsync (not SingleOrDefaultAsync) is safe here because
/// DotMarcDbContext.OnModelCreating's HasData seed guarantees the row exists from the moment the
/// AddNotificationSettings migration runs.</summary>
public static class NotificationSettingsService
{
    public static Task<NotificationSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.NotificationSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, NotificationSettings updated, CancellationToken cancellationToken = default)
    {
        ValidateWebhookUrl(updated.TeamsWebhookUrl, "Teams webhook URL");
        ValidateWebhookUrl(updated.SlackWebhookUrl, "Slack webhook URL");
        ValidateWebhookUrl(updated.GenericWebhookUrl, "Generic webhook URL");
        if (updated.AcknowledgeableAutoCloseDays is < 0 or > 365)
        {
            throw new ArgumentException("Close policy and nameserver alerts after (days) must be from 0 to 365.", nameof(updated));
        }

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.NotificationSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Teams", saved.TeamsEnabled, updated.TeamsEnabled)
            .Field("Slack", saved.SlackEnabled, updated.SlackEnabled)
            .Field("Generic webhook", saved.GenericWebhookEnabled, updated.GenericWebhookEnabled)
            // Webhook URLs carry the token that lets anyone post to the channel, so they're recorded like secrets.
            .Secret("Teams webhook URL", saved.TeamsWebhookUrl != updated.TeamsWebhookUrl)
            .Secret("Slack webhook URL", saved.SlackWebhookUrl != updated.SlackWebhookUrl)
            .Secret("Generic webhook URL", saved.GenericWebhookUrl != updated.GenericWebhookUrl)
            .Field("Missing report threshold (days)", saved.MissingReportThresholdDays, updated.MissingReportThresholdDays)
            .Field("Cooldown (minutes)", saved.CooldownMinutes, updated.CooldownMinutes)
            .Field("Monitor interval (seconds)", saved.MonitorIntervalSeconds, updated.MonitorIntervalSeconds)
            .Field("Suspicious reject minimum volume", saved.SuspiciousRejectMinVolume, updated.SuspiciousRejectMinVolume)
            .Field("Suspicious reject non-benign %", saved.SuspiciousRejectNonBenignPercent, updated.SuspiciousRejectNonBenignPercent)
            .Field("DMARC record alerts", saved.DmarcAlertMode, updated.DmarcAlertMode)
            .Field("DMARC authorization record alerts", saved.DmarcAuthorizationAlertMode, updated.DmarcAuthorizationAlertMode)
            .Field("TLS-RPT record alerts", saved.TlsrptAlertMode, updated.TlsrptAlertMode)
            .Field("SPF alerts", saved.SpfAlertMode, updated.SpfAlertMode)
            .Field("MX alerts", saved.MxAlertMode, updated.MxAlertMode)
            .Field("DKIM alerts", saved.DkimAlertMode, updated.DkimAlertMode)
            .Field("MTA-STS alerts", saved.MtaStsAlertMode, updated.MtaStsAlertMode)
            .Field("DMARC policy weakened alerts", saved.DmarcPolicyWeakenedEnabled, updated.DmarcPolicyWeakenedEnabled)
            .Field("Nameservers changed alerts", saved.NameserversChangedEnabled, updated.NameserversChangedEnabled)
            .Field("Close policy and nameserver alerts after (days)", saved.AcknowledgeableAutoCloseDays, updated.AcknowledgeableAutoCloseDays);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.NotificationSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        existing.Enabled = updated.Enabled;
        existing.TeamsEnabled = updated.TeamsEnabled;
        existing.TeamsWebhookUrl = updated.TeamsWebhookUrl;
        existing.SlackEnabled = updated.SlackEnabled;
        existing.SlackWebhookUrl = updated.SlackWebhookUrl;
        existing.GenericWebhookEnabled = updated.GenericWebhookEnabled;
        existing.GenericWebhookUrl = updated.GenericWebhookUrl;
        existing.MissingReportThresholdDays = updated.MissingReportThresholdDays;
        existing.CooldownMinutes = updated.CooldownMinutes;
        existing.MonitorIntervalSeconds = updated.MonitorIntervalSeconds;
        existing.SuspiciousRejectMinVolume = updated.SuspiciousRejectMinVolume;
        existing.SuspiciousRejectNonBenignPercent = updated.SuspiciousRejectNonBenignPercent;
        existing.DmarcAlertMode = updated.DmarcAlertMode;
        existing.DmarcAuthorizationAlertMode = updated.DmarcAuthorizationAlertMode;
        existing.TlsrptAlertMode = updated.TlsrptAlertMode;
        existing.SpfAlertMode = updated.SpfAlertMode;
        existing.MxAlertMode = updated.MxAlertMode;
        existing.DkimAlertMode = updated.DkimAlertMode;
        existing.MtaStsAlertMode = updated.MtaStsAlertMode;
        existing.DmarcPolicyWeakenedEnabled = updated.DmarcPolicyWeakenedEnabled;
        existing.NameserversChangedEnabled = updated.NameserversChangedEnabled;
        existing.AcknowledgeableAutoCloseDays = updated.AcknowledgeableAutoCloseDays;

        AuditLog.Record(context, actor, AuditActions.NotificationSettingsSaved, AuditTarget.Settings("Notifications"), "Saved notification settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateWebhookUrl(string? value, string settingName)
    {
        if (WebhookUrls.Problem(value, settingName) is { } problem)
        {
            throw new ArgumentException(problem, nameof(value));
        }
    }
}
