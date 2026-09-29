using System.Globalization;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Audit;

/// <summary>Reads and saves the audit retention periods. Follows NotificationSettingsService's pattern.</summary>
public static class AuditSettingsService
{
    public static Task<AuditSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.AuditSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, AuditSettings updated, CancellationToken cancellationToken = default)
    {
        Validate(updated.ChangeRetentionDays, "Changes");
        Validate(updated.SignInRetentionDays, "Sign-ins");
        Validate(updated.PageViewRetentionDays, "Page views");

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.AuditSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Keep changes for", Describe(saved.ChangeRetentionDays), Describe(updated.ChangeRetentionDays))
            .Field("Keep sign-ins for", Describe(saved.SignInRetentionDays), Describe(updated.SignInRetentionDays))
            .Field("Keep page views for", Describe(saved.PageViewRetentionDays), Describe(updated.PageViewRetentionDays));
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.AuditSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ChangeRetentionDays = updated.ChangeRetentionDays;
        existing.SignInRetentionDays = updated.SignInRetentionDays;
        existing.PageViewRetentionDays = updated.PageViewRetentionDays;

        AuditLog.Record(context, actor, AuditActions.AuditSettingsSaved, AuditTarget.Settings("Audit log retention"), "Changed how long audit entries are kept", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(int? days) => days is { } count
        ? $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "day" : "days")}"
        : "Keep forever";

    private static void Validate(int? days, string kindName)
    {
        if (days is { } count && (count < AuditSettings.MinimumRetentionDays || count > AuditSettings.MaximumRetentionDays))
        {
            throw new ArgumentOutOfRangeException(nameof(days), count,
                $"{kindName} must be kept for between {AuditSettings.MinimumRetentionDays} and {AuditSettings.MaximumRetentionDays} days, or forever.");
        }
    }
}
