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

    public static async Task SaveAsync(DotMarcDbContext context, NotificationSettings updated, CancellationToken cancellationToken = default)
    {
        var existing = await context.NotificationSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        existing.Enabled = updated.Enabled;
        existing.DeliveryMode = updated.DeliveryMode;
        existing.TeamsWebhookUrl = updated.TeamsWebhookUrl;
        existing.GenericWebhookUrl = updated.GenericWebhookUrl;
        existing.MissingReportThresholdDays = updated.MissingReportThresholdDays;
        existing.CooldownMinutes = updated.CooldownMinutes;
        existing.MonitorIntervalSeconds = updated.MonitorIntervalSeconds;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
