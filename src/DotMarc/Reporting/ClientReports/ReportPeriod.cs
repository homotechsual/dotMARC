using System.Globalization;

namespace DotMarc.Reporting.ClientReports;

public enum ReportFrequency { Off, Weekly, Monthly, Quarterly }

public enum ReportPeriodKind { Week, Month, Quarter, Custom }

/// <summary>The days a report covers, inclusive, in the report time zone.</summary>
public sealed record ReportPeriod(DateOnly Start, DateOnly End, ReportPeriodKind Kind)
{
    public int Days => End.DayNumber - Start.DayNumber + 1;

    public string Label => Kind switch
    {
        ReportPeriodKind.Week => $"Week of {Format(Start)}",
        ReportPeriodKind.Month => Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        ReportPeriodKind.Quarter => string.Create(CultureInfo.InvariantCulture, $"Q{((Start.Month - 1) / 3) + 1} {Start.Year}"),
        _ => $"{Format(Start)} to {Format(End)}",
    };

    public static string Format(DateOnly day) => day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Period maths in the report time zone. Weeks run Monday to Sunday; quarters start in January, April, July
/// and October; every boundary is local midnight.</summary>
public static class ReportPeriods
{
    public const int MaximumDays = 366;

    public static DateOnly LocalDay(DateTimeOffset utc, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    public static ReportPeriod Week(DateOnly monday) => new(monday, monday.AddDays(6), ReportPeriodKind.Week);

    public static ReportPeriod Month(DateOnly first) => new(first, first.AddMonths(1).AddDays(-1), ReportPeriodKind.Month);

    public static ReportPeriod Quarter(DateOnly first) => new(first, first.AddMonths(3).AddDays(-1), ReportPeriodKind.Quarter);

    /// <summary>The last complete period of the frequency at <paramref name="nowUtc"/>.</summary>
    public static ReportPeriod Previous(ReportFrequency frequency, TimeZoneInfo zone, DateTimeOffset nowUtc) => frequency switch
    {
        ReportFrequency.Weekly => Recent(ReportPeriodKind.Week, zone, nowUtc, 1)[0],
        ReportFrequency.Monthly => Recent(ReportPeriodKind.Month, zone, nowUtc, 1)[0],
        ReportFrequency.Quarterly => Recent(ReportPeriodKind.Quarter, zone, nowUtc, 1)[0],
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "A schedule that's off has no period."),
    };

    /// <summary>The last <paramref name="count"/> complete periods of a kind, newest first, for the period picker.</summary>
    public static IReadOnlyList<ReportPeriod> Recent(ReportPeriodKind kind, TimeZoneInfo zone, DateTimeOffset nowUtc, int count)
    {
        var today = LocalDay(nowUtc, zone);
        var current = kind switch
        {
            ReportPeriodKind.Week => Week(today.AddDays(-(((int)today.DayOfWeek + 6) % 7))),
            ReportPeriodKind.Month => Month(new DateOnly(today.Year, today.Month, 1)),
            ReportPeriodKind.Quarter => Quarter(new DateOnly(today.Year, (((today.Month - 1) / 3) * 3) + 1, 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Custom ranges aren't listed."),
        };
        var periods = new List<ReportPeriod>();
        for (var step = 1; step <= count; step++)
        {
            periods.Add(kind switch
            {
                ReportPeriodKind.Week => Week(current.Start.AddDays(-7 * step)),
                ReportPeriodKind.Month => Month(current.Start.AddMonths(-step)),
                _ => Quarter(current.Start.AddMonths(-3 * step)),
            });
        }

        return periods;
    }

    /// <summary>A range picked by hand, recognised as a week, month or quarter when it is exactly one.</summary>
    public static ReportPeriod FromRange(DateOnly start, DateOnly end, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (start > end) throw new ArgumentException("The start date must be on or before the end date.", nameof(start));
        if (end >= LocalDay(nowUtc, zone)) throw new ArgumentException("A report can only cover days that have ended.", nameof(end));
        if (end.DayNumber - start.DayNumber + 1 > MaximumDays) throw new ArgumentException($"A report can cover at most {MaximumDays} days.", nameof(end));

        if (start.DayOfWeek == DayOfWeek.Monday && end == start.AddDays(6)) return Week(start);
        if (start.Day == 1 && end == start.AddMonths(1).AddDays(-1)) return Month(start);
        if (start.Day == 1 && (start.Month - 1) % 3 == 0 && end == start.AddMonths(3).AddDays(-1)) return Quarter(start);
        return new ReportPeriod(start, end, ReportPeriodKind.Custom);
    }

    /// <summary>What a period's figures are compared with: the previous week, month or quarter, or for a custom range
    /// the equal-length range just before it.</summary>
    public static ReportPeriod PreviousForComparison(ReportPeriod period) => period.Kind switch
    {
        ReportPeriodKind.Week => Week(period.Start.AddDays(-7)),
        ReportPeriodKind.Month => Month(period.Start.AddMonths(-1)),
        ReportPeriodKind.Quarter => Quarter(period.Start.AddMonths(-3)),
        _ => new ReportPeriod(period.Start.AddDays(-period.Days), period.Start.AddDays(-1), ReportPeriodKind.Custom),
    };

    public static DateTimeOffset StartUtc(ReportPeriod period, TimeZoneInfo zone) => LocalTimeToUtc(period.Start, 0, zone);

    /// <summary>The instant just after the period, exclusive.</summary>
    public static DateTimeOffset EndUtc(ReportPeriod period, TimeZoneInfo zone) => LocalTimeToUtc(period.End.AddDays(1), 0, zone);

    public static DateTimeOffset DueUtc(ReportPeriod period, TimeZoneInfo zone, int sendHour) => LocalTimeToUtc(period.End.AddDays(1), sendHour, zone);

    /// <summary>A local day and hour as a UTC instant. A local time skipped by a clock change becomes the first valid
    /// minute after it; one that happens twice takes the first occurrence.</summary>
    private static DateTimeOffset LocalTimeToUtc(DateOnly day, int hour, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
