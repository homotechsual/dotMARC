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
        ValidateWebhookUrl(updated.GenericWebhookUrl, "Generic webhook URL");

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.NotificationSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Delivery mode", saved.DeliveryMode, updated.DeliveryMode)
            // Webhook URLs carry the token that lets anyone post to the channel, so they're recorded like secrets.
            .Secret("Teams webhook URL", saved.TeamsWebhookUrl != updated.TeamsWebhookUrl)
            .Secret("Generic webhook URL", saved.GenericWebhookUrl != updated.GenericWebhookUrl)
            .Field("Missing report threshold (days)", saved.MissingReportThresholdDays, updated.MissingReportThresholdDays)
            .Field("Cooldown (minutes)", saved.CooldownMinutes, updated.CooldownMinutes)
            .Field("Monitor interval (seconds)", saved.MonitorIntervalSeconds, updated.MonitorIntervalSeconds)
            .Field("Suspicious reject minimum volume", saved.SuspiciousRejectMinVolume, updated.SuspiciousRejectMinVolume)
            .Field("Suspicious reject non-benign %", saved.SuspiciousRejectNonBenignPercent, updated.SuspiciousRejectNonBenignPercent);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.NotificationSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        existing.Enabled = updated.Enabled;
        existing.DeliveryMode = updated.DeliveryMode;
        existing.TeamsWebhookUrl = updated.TeamsWebhookUrl;
        existing.GenericWebhookUrl = updated.GenericWebhookUrl;
        existing.MissingReportThresholdDays = updated.MissingReportThresholdDays;
        existing.CooldownMinutes = updated.CooldownMinutes;
        existing.MonitorIntervalSeconds = updated.MonitorIntervalSeconds;
        existing.SuspiciousRejectMinVolume = updated.SuspiciousRejectMinVolume;
        existing.SuspiciousRejectNonBenignPercent = updated.SuspiciousRejectNonBenignPercent;

        AuditLog.Record(context, actor, AuditActions.NotificationSettingsSaved, AuditTarget.Settings("Notifications"), "Saved notification settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateWebhookUrl(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"{settingName} must be an absolute HTTPS URL without embedded credentials.", nameof(value));
        }
    }
}
