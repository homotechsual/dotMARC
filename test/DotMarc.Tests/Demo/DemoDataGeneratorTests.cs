// test/DotMarc.Tests/Demo/DemoDataGeneratorTests.cs
using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoDataGeneratorTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private static DemoDataset Generate() => DemoDataGenerator.Generate(new Random(42), NowUtc);

    [Fact]
    public void GeneratesExactlySevenDomainsAcrossFourGroups()
    {
        var dataset = Generate();

        Assert.Equal(4, dataset.Groups.Count);
        Assert.Equal(7, dataset.Domains.Count);
        Assert.Contains(dataset.Domains, d => d.GroupName is null);
    }

    [Fact]
    public void AuroraRetailDomains_StayConsistentlyHealthy()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "aurora-retail.example");

        var passRate = DomainStatistics.GetPassRate(ToReports(domain));
        Assert.True(passRate is > 0.99, $"expected >99% pass rate, got {passRate}");
        Assert.Equal("Aurora Retail", domain.GroupName);
    }

    [Fact]
    public void BrightlineLegal_PassRateClimbsAcrossTheWindow()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "brightline-legal.example");

        var firstWeek = domain.Reports.Where(r => r.DateRangeBeginUtc < NowUtc.AddDays(-DemoDataGenerator.HistoryDays + 7)).ToList();
        var lastWeek = domain.Reports.Where(r => r.DateRangeBeginUtc >= NowUtc.AddDays(-7)).ToList();

        var earlyPassRate = DomainStatistics.GetPassRate(ToReports(domain, firstWeek))!.Value;
        var latePassRate = DomainStatistics.GetPassRate(ToReports(domain, lastWeek))!.Value;

        Assert.True(latePassRate > earlyPassRate, $"expected pass rate to climb: early={earlyPassRate}, late={latePassRate}");
        Assert.True(latePassRate >= 0.95, $"expected the domain to read as healthy by the end of the window, got {latePassRate}");
    }

    [Fact]
    public void CobaltFreight_FirstDomainReadsAsWarning_DueToAPersistentFailingSource()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "cobalt-freight.example");

        var passRate = DomainStatistics.GetPassRate(ToReports(domain))!.Value;
        Assert.True(passRate < 0.95, $"expected a Warning-level pass rate (<95%), got {passRate}");

        var sources = DomainStatistics.GetSourceAggregates(ToReports(domain));
        Assert.Contains(sources, s => s.SpfResult == AuthResult.Fail && s.DkimResult == AuthResult.Fail);
    }

    [Fact]
    public void CobaltFreight_SecondDomainHasNoReportsInTheLastThreeDays()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "fleet.cobalt-freight.example");

        Assert.NotNull(domain.LastReportReceivedUtc);
        Assert.True(domain.LastReportReceivedUtc < NowUtc.AddDays(-2), "expected this domain to read as Missing");
    }

    [Fact]
    public void DriftwoodMedia_HasAMissingAuthorizationRecordStatus()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "driftwood-media.example");

        Assert.Equal(DmarcCheckStatus.MissingAuthorizationRecord, domain.DmarcCheckStatus);
        Assert.False(string.IsNullOrWhiteSpace(domain.DmarcCheckDetail));
    }

    [Fact]
    public void PollCycles_CoverTheLast7DaysRaw_AndOlderDaysAreDailySummariesOnly()
    {
        var dataset = Generate();

        Assert.NotEmpty(dataset.PollCycles);
        Assert.All(dataset.PollCycles, p => Assert.True(p.PolledUtc >= NowUtc.AddDays(-7)));
        Assert.Contains(dataset.PollCycles, p => !p.Succeeded);

        Assert.NotEmpty(dataset.PollCycleDailySummaries);
        Assert.All(dataset.PollCycleDailySummaries, s => Assert.True(s.Date < DateOnly.FromDateTime(NowUtc.AddDays(-7).UtcDateTime)));
    }

    [Fact]
    public void ParseFailures_AreNotEmpty()
    {
        var dataset = Generate();
        Assert.NotEmpty(dataset.ParseFailures);
    }

    [Fact]
    public void SameSeedAndTime_ProducesTheSameDataset()
    {
        var first = DemoDataGenerator.Generate(new Random(7), NowUtc);
        var second = DemoDataGenerator.Generate(new Random(7), NowUtc);

        Assert.Equal(first.Domains.Select(d => d.Reports.Count), second.Domains.Select(d => d.Reports.Count));
    }

    private static List<Report> ToReports(DemoDomainSeed domain, List<DemoReportSeed>? reports = null) =>
        (reports ?? domain.Reports).Select(r => new Report
        {
            Domain = null!,
            ReportingOrg = r.ReportingOrg,
            ReportId = r.ReportId,
            DateRangeBeginUtc = r.DateRangeBeginUtc,
            DateRangeEndUtc = r.DateRangeEndUtc,
            RawXml = "",
            ReceivedUtc = r.DateRangeEndUtc,
            Records = r.Records.Select(rec => new ReportRecord
            {
                Report = null!,
                SourceIp = rec.SourceIp,
                MessageCount = rec.MessageCount,
                Disposition = rec.Disposition,
                SpfResult = rec.SpfResult,
                DkimResult = rec.DkimResult,
                HeaderFrom = domain.Name
            }).ToList()
        }).ToList();
}
