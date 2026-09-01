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

    /// <summary>The Dashboard classifies a domain as "OK" only when DashboardSummary.Build's
    /// DomainStatistics.GetPassRate — a volume-weighted average over the FULL 30-day window,
    /// not just the most recent week — clears 0.95. driftwood-events.example (the ungrouped
    /// domain, meant only to demonstrate the "no group" dashboard case) and
    /// brightline-legal.example (meant to read as "ramped up and now healthy") both need to
    /// clear that bar across the whole window, not just at the end of it. This is the test that
    /// would have caught the narrative bug where both instead read as "Warning".</summary>
    [Theory]
    [InlineData("driftwood-events.example")]
    [InlineData("brightline-legal.example")]
    public void ReadsAsHealthyOnTheDashboard_OverTheFull30DayWindow(string domainName)
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == domainName);

        var fullWindowPassRate = DomainStatistics.GetPassRate(ToReports(domain))!.Value;

        Assert.True(fullWindowPassRate >= 0.95, $"expected {domainName} to read as OK (>=95%) over the full window, got {fullWindowPassRate}");
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

    /// <summary>Dashboard.razor prominently shows the most recent poll cycle's status. When the
    /// random roll that normally injects a failure never fires, BuildPollCycles' fallback used
    /// to rewrite cycles[^1] — the MOST RECENT cycle — as a guaranteed failure, making the
    /// demo's landing page show a failed "last poll" on whichever reset happened to miss that
    /// roll. It should rewrite a cycle in the middle of the window instead, so the most recent
    /// poll always reads as healthy. Checked across several seeds so this isn't a lucky draw
    /// with just one.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(19)]
    [InlineData(23)]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(33)] // reproduces the pre-fix bug: the random injection never fires within 3
                      // days for this seed, so the fallback used to rewrite cycles[^1].
    public void PollCycles_LastCycle_IsNeverTheGuaranteedInjectedFailure(int seed)
    {
        var dataset = DemoDataGenerator.Generate(new Random(seed), NowUtc);

        var lastCycle = dataset.PollCycles[^1];

        Assert.True(lastCycle.Succeeded, $"seed {seed}: the most recent poll cycle should never be the guaranteed-failure fallback");
    }

    [Fact]
    public void ParseFailures_AreNotEmpty()
    {
        var dataset = Generate();
        Assert.NotEmpty(dataset.ParseFailures);
    }

    /// <summary>Each domain's MTA-STS state is chosen to match its existing DMARC narrative rather
    /// than being arbitrary: the flagship healthy client is fully rolled out, the struggling
    /// client's MTA-STS has regressed too, etc. — see DemoDataGenerator.BuildDomain's MTA-STS
    /// parameters.</summary>
    [Theory]
    [InlineData("aurora-retail.example", true, MtaStsStatus.Active, MtaStsMode.Enforce)]
    [InlineData("shop.aurora-retail.example", true, MtaStsStatus.Active, MtaStsMode.Testing)]
    [InlineData("brightline-legal.example", true, MtaStsStatus.PendingCertificate, MtaStsMode.Enforce)]
    [InlineData("cobalt-freight.example", true, MtaStsStatus.Failed, MtaStsMode.Enforce)]
    [InlineData("fleet.cobalt-freight.example", true, MtaStsStatus.PendingDns, MtaStsMode.Testing)]
    [InlineData("driftwood-media.example", false, MtaStsStatus.NotConfigured, MtaStsMode.Testing)]
    [InlineData("driftwood-events.example", false, MtaStsStatus.NotConfigured, MtaStsMode.Testing)]
    public void MtaStsNarrative_MatchesEachDomainsStory(string domainName, bool expectedEnabled, MtaStsStatus expectedStatus, MtaStsMode expectedMode)
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == domainName);

        Assert.Equal(expectedEnabled, domain.MtaStsEnabled);
        Assert.Equal(expectedStatus, domain.MtaStsStatus);
        Assert.Equal(expectedMode, domain.MtaStsMode);
    }

    [Fact]
    public void CobaltFreight_MtaStsFailure_HasADetailMessage()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "cobalt-freight.example");

        Assert.False(string.IsNullOrWhiteSpace(domain.MtaStsCheckDetail));
    }

    [Theory]
    [InlineData("driftwood-media.example")]
    [InlineData("driftwood-events.example")]
    public void NotConfiguredDomains_HaveNoMxHostsOrCheckedTimestamp(string domainName)
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == domainName);

        Assert.Empty(domain.MtaStsMxHosts);
        Assert.Null(domain.MtaStsCheckedUtc);
    }

    [Theory]
    [InlineData("aurora-retail.example")]
    [InlineData("shop.aurora-retail.example")]
    [InlineData("brightline-legal.example")]
    [InlineData("cobalt-freight.example")]
    [InlineData("fleet.cobalt-freight.example")]
    public void EnabledDomains_HaveMxHostsAndACheckedTimestamp(string domainName)
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == domainName);

        Assert.NotEmpty(domain.MtaStsMxHosts);
        Assert.NotNull(domain.MtaStsCheckedUtc);
    }

    /// <summary>ProblemSourceIp used to derive its IP from domainName.GetHashCode(), which .NET
    /// Core randomizes per process — a different IP on every container restart, contradicting
    /// this class's own reproducibility doc comment and the design spec's "a given day's dataset
    /// is reproducible if the container restarts without crossing a reset boundary." It's now
    /// derived from the domain's fixed sortOrder instead, which doesn't depend on Random at all
    /// — so the same domain gets the same problem-source IP regardless of which seed generated
    /// the rest of that day's dataset.</summary>
    [Fact]
    public void ProblemSourceIp_IsTheSameForAGivenDomain_AcrossDifferentRandomSeeds()
    {
        var first = DemoDataGenerator.Generate(new Random(1), NowUtc);
        var second = DemoDataGenerator.Generate(new Random(999), NowUtc);

        var firstIps = ProblemSourceIpsFor(first, "cobalt-freight.example");
        var secondIps = ProblemSourceIpsFor(second, "cobalt-freight.example");

        // Every failing record for this domain, across the whole 30-day window, uses the exact
        // same source IP within one generation (it's derived from the domain's fixed sortOrder,
        // not from any random draw) — and that IP is identical across two entirely different
        // seeds, proving it no longer depends on the per-process-randomized
        // string.GetHashCode() it used to.
        Assert.Single(firstIps);
        Assert.Single(secondIps);
        Assert.Equal(firstIps, secondIps);
    }

    private static HashSet<string> ProblemSourceIpsFor(DemoDataset dataset, string domainName) =>
        [.. dataset.Domains.Single(d => d.Name == domainName).Reports
            .SelectMany(r => r.Records)
            .Where(r => r.SpfResult == AuthResult.Fail && r.DkimResult == AuthResult.Fail)
            .Select(r => r.SourceIp)];

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
