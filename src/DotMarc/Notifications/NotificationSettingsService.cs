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
            .Field("Cooldown (minutes)", saved.CooldownMinutes, updated.CooldownMinutes)
            .Field("Monitor interval (seconds)", saved.MonitorIntervalSeconds, updated.MonitorIntervalSeconds);
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
        existing.CooldownMinutes = updated.CooldownMinutes;
        existing.MonitorIntervalSeconds = updated.MonitorIntervalSeconds;

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
