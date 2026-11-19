using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Read/update the singleton NotificationSettings row. Follows this project's
/// DomainManagementService convention of a static class operating on a caller-supplied
/// DotMarcDbContext. The Teams, Slack and generic webhook channels are retired: they live on as AlertDestination rows, and this only
/// saves the master switch, the cooldown and the monitor interval. SingleAsync (not SingleOrDefaultAsync) is safe here because
/// DotMarcDbContext.OnModelCreating's HasData seed guarantees the row exists from the moment the
/// AddNotificationSettings migration runs.</summary>
public static class NotificationSettingsService
{
    public static Task<NotificationSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.NotificationSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, NotificationSettings updated, CancellationToken cancellationToken = default)
    {
        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.NotificationSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Cooldown (minutes)", saved.CooldownMinutes, updated.CooldownMinutes)
            .Field("Monitor interval (seconds)", saved.MonitorIntervalSeconds, updated.MonitorIntervalSeconds);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.NotificationSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        existing.Enabled = updated.Enabled;
        existing.CooldownMinutes = updated.CooldownMinutes;
        existing.MonitorIntervalSeconds = updated.MonitorIntervalSeconds;

        AuditLog.Record(context, actor, AuditActions.NotificationSettingsSaved, AuditTarget.Settings("Notifications"), "Saved notification settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
