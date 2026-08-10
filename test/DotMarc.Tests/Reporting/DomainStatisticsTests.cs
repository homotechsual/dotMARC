using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public class DomainStatisticsTests
{
    private static Report ReportWith(params ReportRecord[] records)
    {
        var report = new Report
        {
            ReportingOrg = "google.com",
            ReportId = Guid.NewGuid().ToString(),
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow
        };
        report.Records.AddRange(records);
        return report;
    }

    private static ReportRecord Record(string sourceIp, int count, AuthResult spf, AuthResult dkim, DispositionResult disposition = DispositionResult.None) =>
        new()
        {
            SourceIp = sourceIp,
            MessageCount = count,
            SpfResult = spf,
            DkimResult = dkim,
            Disposition = disposition,
            HeaderFrom = "contoso.io"
        };

    [Fact]
    public void GetPassRate_ReturnsNull_WhenNoVolumeInWindow()
    {
        Assert.Null(DomainStatistics.GetPassRate([]));
    }

    [Fact]
    public void GetPassRate_IsVolumeWeighted_NotRecordAveraged()
    {
        // One record with 90 passing messages, one with 10 failing: record-averaging would give
        // 50%, volume-weighting gives 90%.
        var report = ReportWith(
            Record("198.51.100.1", 90, AuthResult.Pass, AuthResult.Pass),
            Record("198.51.100.2", 10, AuthResult.Fail, AuthResult.Fail));

        var rate = DomainStatistics.GetPassRate([report]);

        Assert.Equal(0.9, rate);
    }

    [Fact]
    public void GetOverallPassRate_WeightsAcrossDomainsByVolume_NotByAveragingDomainRates()
    {
        // Domain A: 100% pass rate on 10 messages. Domain B: 0% pass rate on 990 messages.
        // Equal-weighting the two domain rates gives 50%; volume-weighting gives 1%.
        var domainA = ReportWith(Record("198.51.100.1", 10, AuthResult.Pass, AuthResult.Pass));
        var domainB = ReportWith(Record("198.51.100.2", 990, AuthResult.Fail, AuthResult.Fail));

        var overall = DomainStatistics.GetOverallPassRate([[domainA], [domainB]]);

        Assert.Equal(0.01, overall, precision: 3);
    }

    [Fact]
    public void GetOverallPassRate_ReturnsZero_WhenNoVolumeAnywhere()
    {
        Assert.Equal(0, DomainStatistics.GetOverallPassRate([]));
    }

    [Fact]
    public void GetSourceAggregates_SumsVolumePerSource()
    {
        var reportA = ReportWith(Record("198.51.100.1", 5, AuthResult.Pass, AuthResult.Pass));
        var reportB = ReportWith(Record("198.51.100.1", 7, AuthResult.Pass, AuthResult.Pass));

        var aggregates = DomainStatistics.GetSourceAggregates([reportA, reportB]);

        var source = Assert.Single(aggregates);
        Assert.Equal("198.51.100.1", source.SourceIp);
        Assert.Equal(12, source.Volume);
    }

    [Fact]
    public void GetSourceAggregates_CombinesAuthResults_AsPassIfAnyRecordPassed()
    {
        var reportA = ReportWith(Record("198.51.100.1", 5, AuthResult.Fail, AuthResult.Fail));
        var reportB = ReportWith(Record("198.51.100.1", 5, AuthResult.Pass, AuthResult.Pass));

        var source = Assert.Single(DomainStatistics.GetSourceAggregates([reportA, reportB]));

        Assert.Equal(AuthResult.Pass, source.SpfResult);
        Assert.Equal(AuthResult.Pass, source.DkimResult);
    }

    [Fact]
    public void GetSourceAggregates_CombinesDisposition_AsMostSevereSeen()
    {
        var reportA = ReportWith(Record("198.51.100.1", 5, AuthResult.Pass, AuthResult.Pass, DispositionResult.None));
        var reportB = ReportWith(Record("198.51.100.1", 5, AuthResult.Fail, AuthResult.Fail, DispositionResult.Quarantine));
        var reportC = ReportWith(Record("198.51.100.1", 5, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject));

        var source = Assert.Single(DomainStatistics.GetSourceAggregates([reportA, reportB, reportC]));

        Assert.Equal(DispositionResult.Reject, source.Disposition);
    }

    [Fact]
    public void GetSourceAggregates_KeepsDifferentSourcesSeparate()
    {
        var report = ReportWith(
            Record("198.51.100.1", 5, AuthResult.Pass, AuthResult.Pass),
            Record("198.51.100.2", 3, AuthResult.Fail, AuthResult.Fail));

        var aggregates = DomainStatistics.GetSourceAggregates([report]);

        Assert.Equal(2, aggregates.Count);
    }

    [Fact]
    public void GetWindowCutoffUtc_Is30DaysBeforeNow()
    {
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        var cutoff = DomainStatistics.GetWindowCutoffUtc(now);

        Assert.Equal(now.AddDays(-30), cutoff);
    }
}
