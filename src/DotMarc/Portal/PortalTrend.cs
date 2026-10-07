using DotMarc.Data;
using DotMarc.Reporting;

namespace DotMarc.Portal;

public static class PortalTrend
{
    /// <summary>The DMARC pass rate for each of the last <paramref name="days"/> days (UTC), oldest first. A day with no
    /// mail reported is null, so the trend line shows a gap instead of a misleading zero.</summary>
    public static IReadOnlyList<double?> DailyPassRates(IEnumerable<Report> reportsInWindow, int days, DateTimeOffset nowUtc)
    {
        var today = nowUtc.UtcDateTime.Date;
        var byDay = reportsInWindow
            .GroupBy(report => report.ReceivedUtc.UtcDateTime.Date)
            .ToDictionary(sameDay => sameDay.Key, sameDay => sameDay.ToList());
        return Enumerable.Range(0, days)
            .Select(offset => today.AddDays(offset - days + 1))
            .Select(day => byDay.TryGetValue(day, out var reports) ? DomainStatistics.GetPassRate(reports) : null)
            .ToList();
    }
}
