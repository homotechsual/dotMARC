using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class DispositionTrendTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static Report ReportOn(DateOnly day, params (DispositionResult Disposition, int Count)[] records) => new()
    {
        ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>",
        ReceivedUtc = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero),
        Records = records.Select(record => new ReportRecord { SourceIp = "192.0.2.1", HeaderFrom = "contoso.com", Disposition = record.Disposition, MessageCount = record.Count }).ToList(),
    };

    [Fact]
    public void WithNoReportsInTheWindow_ThereIsNoData()
    {
        var trend = DispositionTrend.Build([ReportOn(Today.AddDays(-40), (DispositionResult.None, 5))], Today);

        Assert.False(trend.HasData);
    }

    [Fact]
    public void EachLine_CountsItsOwnDisposition_OnTheDayReceived()
    {
        var trend = DispositionTrend.Build(
            [ReportOn(Today, (DispositionResult.None, 5), (DispositionResult.Reject, 2)), ReportOn(Today.AddDays(-1), (DispositionResult.Quarantine, 3))], Today);

        Assert.True(trend.HasData);
        Assert.Equal(30, trend.Labels.Count);
        Assert.Equal([5d, 0d, 2d], trend.Series.Select(series => series.Data[29].Y));
        Assert.Equal([0d, 3d, 0d], trend.Series.Select(series => series.Data[28].Y));
    }

    [Fact]
    public void OnlyEveryFifthDayAndToday_AreLabelled_SoTheDatesDontOverlap()
    {
        var labels = DispositionTrend.Build([], Today).Labels;

        Assert.Equal("10/14", labels[29]);
        Assert.Equal("10/09", labels[24]);
        Assert.Equal("09/19", labels[4]);
        Assert.Equal("", labels[0]);
        Assert.Equal("", labels[28]);
        Assert.Equal(6, labels.Count(label => label.Length > 0));
    }
}
