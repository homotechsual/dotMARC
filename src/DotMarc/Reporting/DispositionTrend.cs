using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>A domain's last 30 days of reported mail by what receivers did with it, one line per disposition, bucketed by
/// the day each report was received (a record has no timestamp of its own). HasData is false when nothing arrived in
/// the window, so the page can say so instead of drawing three flat lines on top of each other.</summary>
public sealed record DispositionTrend(IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries<double>> Series, bool HasData)
{
    private const int Days = 30;

    // Every fifth day, counted back from today so today always has its date: thirty dates under a page-width chart run
    // into each other.
    private const int LabelEvery = 5;

    public static DispositionTrend Build(IEnumerable<Report> reports, DateOnly today)
    {
        var days = Enumerable.Range(0, Days).Select(offset => today.AddDays(offset - (Days - 1))).ToList();
        var buckets = reports
            .SelectMany(report => report.Records.Select(record => (Day: DateOnly.FromDateTime(report.ReceivedUtc.UtcDateTime), record.Disposition, record.MessageCount)))
            .Where(entry => entry.Day >= days[0] && entry.Day <= today)
            .GroupBy(entry => (entry.Day, entry.Disposition))
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.MessageCount));

        double[] Line(DispositionResult disposition) =>
            days.Select(day => (double)buckets.GetValueOrDefault((day, disposition))).ToArray();

        var labels = days.Select((day, index) => (Days - 1 - index) % LabelEvery == 0 ? day.ToString("MM/dd", System.Globalization.CultureInfo.InvariantCulture) : "").ToList();
        ChartSeries<double>[] series =
        [
            new() { Name = "Passed (none)", Data = Line(DispositionResult.None) },
            new() { Name = "Quarantined", Data = Line(DispositionResult.Quarantine) },
            new() { Name = "Rejected", Data = Line(DispositionResult.Reject) },
        ];
        return new DispositionTrend(labels, series, buckets.Values.Any(count => count > 0));
    }
}
