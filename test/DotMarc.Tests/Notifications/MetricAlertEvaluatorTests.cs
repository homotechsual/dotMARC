using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class MetricAlertEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static ReportRecord Record(int messages, bool passing = true, DispositionResult disposition = DispositionResult.None) => new()
    {
        SourceIp = "203.0.113.5",
        HeaderFrom = "contoso.io",
        MessageCount = messages,
        Disposition = disposition,
        SpfResult = passing ? AuthResult.Pass : AuthResult.Fail,
        DkimResult = passing ? AuthResult.Pass : AuthResult.Fail
    };

    private static Report ReportAt(DateTimeOffset receivedUtc, params ReportRecord[] records) => new()
    {
        ReportingOrg = "google.com",
        ReportId = Guid.NewGuid().ToString(),
        RawXml = "<feedback />",
        ReceivedUtc = receivedUtc,
        Records = [.. records]
    };

    private static Report DaysAgo(double days, params ReportRecord[] records) => ReportAt(Now.AddDays(-days), records);

    // ---- Pass rate ----

    [Fact]
    public void PassRate_RaisesBelowTheThreshold_WithTheMessageCountAndPercentage()
    {
        var reports = new[] { DaysAgo(1, Record(80), Record(20, passing: false)) };

        var result = MetricAlertEvaluator.PassRate(reports, windowDays: 7, belowPercent: 90, minimumMessages: 100, Now);

        Assert.True(result.Raise);
        Assert.Equal(100, result.Messages);
        Assert.Equal(80, result.PassPercent, precision: 6);
    }

    [Fact]
    public void PassRate_NeverRaisesOnFewerMessagesThanTheMinimum_HoweverBad()
    {
        var reports = new[] { DaysAgo(1, Record(99, passing: false)) };

        var result = MetricAlertEvaluator.PassRate(reports, 7, 90, 100, Now);

        Assert.False(result.Raise);
        Assert.Equal(99, result.Messages);
    }

    [Fact]
    public void PassRate_DoesNotRaiseExactlyAtTheThreshold()
    {
        var reports = new[] { DaysAgo(1, Record(90), Record(10, passing: false)) };

        Assert.False(MetricAlertEvaluator.PassRate(reports, 7, 90, 100, Now).Raise);
    }

    [Fact]
    public void PassRate_IgnoresReportsOlderThanTheWindow()
    {
        var reports = new[] { DaysAgo(1, Record(100)), DaysAgo(9, Record(500, passing: false)) };

        var result = MetricAlertEvaluator.PassRate(reports, 7, 90, 100, Now);

        Assert.False(result.Raise);
        Assert.Equal(100, result.Messages);
    }

    [Fact]
    public void PassRate_WithNoReports_DoesNotRaise()
    {
        var result = MetricAlertEvaluator.PassRate([], 7, 90, 1, Now);

        Assert.False(result.Raise);
        Assert.Equal(0, result.Messages);
    }

    [Fact]
    public void PassRate_CountsMessagesNotRecords()
    {
        var reports = new[] { DaysAgo(1, Record(1000, passing: false), Record(1)) };

        var result = MetricAlertEvaluator.PassRate(reports, 7, 90, 100, Now);

        Assert.True(result.Raise);
        Assert.Equal(1001, result.Messages);
    }

    // ---- Volume ----

    private static Report[] Volumes(int previous, int current) =>
    [
        DaysAgo(10, Record(previous)),
        DaysAgo(2, Record(current))
    ];

    [Fact]
    public void Volume_Drop_RaisesWhenCurrentFellByTheChange_WithASignedPercentage()
    {
        var result = MetricAlertEvaluator.Volume(Volumes(previous: 100, current: 40), 7, "drop", 50, 100, Now);

        Assert.True(result.Raise);
        Assert.Equal((40, 100), (result.Current, result.Previous));
        Assert.Equal(-60, result.ChangePercent, precision: 6);
    }

    [Fact]
    public void Volume_Drop_DoesNotRaiseForASmallerFall()
    {
        Assert.False(MetricAlertEvaluator.Volume(Volumes(100, 60), 7, "drop", 50, 100, Now).Raise);
    }

    [Fact]
    public void Volume_Drop_RaisesExactlyAtTheBoundary()
    {
        Assert.True(MetricAlertEvaluator.Volume(Volumes(100, 50), 7, "drop", 50, 100, Now).Raise);
    }

    [Theory]
    [InlineData(99, 0)]
    [InlineData(50, 0)]
    public void Volume_NeverRaisesWhenTheEarlierPeriodIsUnderTheMinimum_EvenWhenCurrentIsZero(int previous, int current)
    {
        Assert.False(MetricAlertEvaluator.Volume(Volumes(previous, current), 7, "drop", 50, 100, Now).Raise);
    }

    [Fact]
    public void Volume_NeverRaisesWhenTheEarlierPeriodIsEmpty()
    {
        var onlyCurrent = new[] { DaysAgo(2, Record(500)) };

        Assert.False(MetricAlertEvaluator.Volume(onlyCurrent, 7, "either", 10, 1, Now).Raise);
        Assert.False(MetricAlertEvaluator.Volume([], 7, "either", 10, 1, Now).Raise);
    }

    [Theory]
    [InlineData(150, true)]
    [InlineData(149, false)]
    public void Volume_Rise_RaisesAtTheBoundaryAndNotJustUnder(int current, bool expected)
    {
        var result = MetricAlertEvaluator.Volume(Volumes(100, current), 7, "rise", 50, 100, Now);

        Assert.Equal(expected, result.Raise);
    }

    [Fact]
    public void Volume_Rise_DoesNotRaiseOnADrop_AndDropDoesNotRaiseOnARise()
    {
        Assert.False(MetricAlertEvaluator.Volume(Volumes(100, 10), 7, "rise", 50, 100, Now).Raise);
        Assert.False(MetricAlertEvaluator.Volume(Volumes(100, 300), 7, "drop", 50, 100, Now).Raise);
    }

    [Fact]
    public void Volume_Either_RaisesForABigDropAndForABigRise()
    {
        Assert.True(MetricAlertEvaluator.Volume(Volumes(100, 40), 7, "either", 50, 100, Now).Raise);
        Assert.True(MetricAlertEvaluator.Volume(Volumes(100, 160), 7, "either", 50, 100, Now).Raise);
        Assert.False(MetricAlertEvaluator.Volume(Volumes(100, 110), 7, "either", 50, 100, Now).Raise);
    }

    [Fact]
    public void Volume_AReportExactlyOneWindowBack_BelongsToThePreviousWindow_AndOneExactlyNowToTheCurrent()
    {
        var reports = new[]
        {
            ReportAt(Now.AddDays(-7), Record(100)),
            ReportAt(Now, Record(40)),
            ReportAt(Now.AddDays(-14), Record(1000))
        };

        var result = MetricAlertEvaluator.Volume(reports, 7, "drop", 50, 100, Now);

        Assert.Equal((40, 100), (result.Current, result.Previous));
        Assert.True(result.Raise);
    }

    [Fact]
    public void Volume_CountsMessagesNotRecords()
    {
        var reports = new[] { DaysAgo(10, Record(60), Record(60)), DaysAgo(1, Record(10), Record(10), Record(10)) };

        var result = MetricAlertEvaluator.Volume(reports, 7, "drop", 50, 100, Now);

        Assert.Equal((30, 120), (result.Current, result.Previous));
    }

    // ---- Rejected or quarantined ----

    [Fact]
    public void Rejected_CountsRejectAndQuarantineMessages_UsingMessageCount()
    {
        var reports = new[] { DaysAgo(1, Record(30, disposition: DispositionResult.Reject), Record(25, disposition: DispositionResult.Quarantine)) };

        var result = MetricAlertEvaluator.Rejected(reports, 7, 50, Now);

        Assert.True(result.Raise);
        Assert.Equal(55, result.Messages);
    }

    [Fact]
    public void Rejected_DoesNotRaiseJustUnderTheThreshold_AndRaisesAtIt()
    {
        var under = new[] { DaysAgo(1, Record(49, disposition: DispositionResult.Reject)) };
        var exactly = new[] { DaysAgo(1, Record(50, disposition: DispositionResult.Quarantine)) };

        Assert.False(MetricAlertEvaluator.Rejected(under, 7, 50, Now).Raise);
        Assert.True(MetricAlertEvaluator.Rejected(exactly, 7, 50, Now).Raise);
    }

    [Fact]
    public void Rejected_IgnoresDeliveredMessages_AndOlderReports()
    {
        var reports = new[]
        {
            DaysAgo(1, Record(500), Record(10, disposition: DispositionResult.Reject)),
            DaysAgo(8, Record(500, disposition: DispositionResult.Reject))
        };

        var result = MetricAlertEvaluator.Rejected(reports, 7, 50, Now);

        Assert.False(result.Raise);
        Assert.Equal(10, result.Messages);
    }

    [Fact]
    public void Rejected_WithNoReports_DoesNotRaise()
    {
        Assert.False(MetricAlertEvaluator.Rejected([], 7, 1, Now).Raise);
    }
}
