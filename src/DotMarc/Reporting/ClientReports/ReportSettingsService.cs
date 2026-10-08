using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

public static class ReportSettingsService
{
    public static Task<ReportSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.ReportSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ReportSettings updated, CancellationToken cancellationToken = default)
    {
        updated.TimeZoneId = updated.TimeZoneId?.Trim() ?? "";
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(updated.TimeZoneId, out _)) throw new ArgumentException($"{updated.TimeZoneId} isn't a time zone this server knows.", nameof(updated));
        if (updated.SendHour is < 0 or > 23) throw new ArgumentException("Send hour must be between 0 and 23.", nameof(updated));

        var saved = await context.ReportSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Time zone", saved.TimeZoneId, updated.TimeZoneId)
            .Field("Send hour", saved.SendHour, updated.SendHour);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.ReportSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.TimeZoneId = updated.TimeZoneId;
        existing.SendHour = updated.SendHour;
        AuditLog.Record(context, actor, AuditActions.ReportSettingsSaved, AuditTarget.Settings("Reports"), "Saved report settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The saved zone, or UTC when this server doesn't know it (for example after moving to another OS).</summary>
    public static TimeZoneInfo ResolveZone(string timeZoneId, ILogger? logger = null)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone))
        {
            return zone;
        }

        logger?.LogWarning("The report time zone {TimeZoneId} isn't known on this server, so reports use UTC.", timeZoneId);
        return TimeZoneInfo.Utc;
    }

    /// <summary>IANA zone ids for the picker, whatever the server's own naming (Windows ids are converted).</summary>
    public static IReadOnlyList<string> ListAvailableZones() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId) ? ianaId : null)
            .OfType<string>()
            .Append("UTC")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
