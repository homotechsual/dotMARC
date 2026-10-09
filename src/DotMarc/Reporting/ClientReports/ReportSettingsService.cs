using System.Globalization;
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
        updated.NumberFormat = updated.NumberFormat?.Trim() ?? "";
        if (!ListNumberFormats().Any(format => format.Name == updated.NumberFormat)) throw new ArgumentException($"{updated.NumberFormat} isn't a number format this server knows.", nameof(updated));

        var saved = await context.ReportSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Time zone", saved.TimeZoneId, updated.TimeZoneId)
            .Field("Send hour", saved.SendHour, updated.SendHour)
            .Field("Number format", saved.NumberFormat, updated.NumberFormat);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.ReportSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.TimeZoneId = updated.TimeZoneId;
        existing.SendHour = updated.SendHour;
        existing.NumberFormat = updated.NumberFormat;
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

    public sealed record NumberFormatChoice(string Name, string DisplayName);

    /// <summary>Regions (specific cultures) for the number format picker, by name. A bare language such as "de" isn't
    /// offered, since separators differ between its regions.</summary>
    public static IReadOnlyList<NumberFormatChoice> ListNumberFormats() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Where(culture => culture.Name.Length > 0)
            .Select(culture => new NumberFormatChoice(culture.Name, $"{culture.DisplayName} ({culture.Name})"))
            .DistinctBy(choice => choice.Name)
            .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The saved number format, or the default when this server doesn't know it.</summary>
    public static CultureInfo ResolveNumberFormat(string? name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(name) ? ReportSettings.DefaultNumberFormat : name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo(ReportSettings.DefaultNumberFormat);
        }
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
