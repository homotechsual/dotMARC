using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Reporting;

public class DashboardSummaryTests
{
    private static Report ReportWith(string reportingOrg, params ReportRecord[] records)
    {
        var report = new Report
        {
            ReportingOrg = reportingOrg,
            ReportId = Guid.NewGuid().ToString(),
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow
        };
        report.Records.AddRange(records);
        return report;
    }

    private static ReportRecord Record(int count, AuthResult spf, AuthResult dkim) =>
        new()
        {
            SourceIp = "198.51.100.1",
            MessageCount = count,
            SpfResult = spf,
            DkimResult = dkim,
            Disposition = DispositionResult.None,
            HeaderFrom = "contoso.io"
        };

    private static Domain DomainWith(string name, bool isMonitored, DateTimeOffset? lastReportReceivedUtc, params Report[] reports)
    {
        var domain = new Domain { Name = name, FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = isMonitored, LastReportReceivedUtc = lastReportReceivedUtc };
        domain.Reports.AddRange(reports);
        return domain;
    }

    [Fact]
    public void Build_ReturnsEmptySummaryAndRows_WhenNoDomains()
    {
        var (summary, rows) = DashboardSummary.Build([], parseFailureCount: 0);

        Assert.Equal(new DashboardSummary(0, 0, 0, 0, 0, 0), summary);
        Assert.Empty(rows);
    }

    [Fact]
    public void Build_MarksDomainOk_WhenPassRateAtOrAbove95Percent()
    {
        var domain = DomainWith("contoso.io", isMonitored: true, DateTimeOffset.UtcNow,
            ReportWith("google.com", Record(100, AuthResult.Pass, AuthResult.Pass)));

        var (_, rows) = DashboardSummary.Build([domain], parseFailureCount: 0);

        var row = Assert.Single(rows);
        Assert.Equal("OK", row.Status);
        Assert.Equal(Color.Success, row.StatusColor);
    }

    [Fact]
    public void Build_MarksDomainWarning_WhenPassRateBelow95Percent()
    {
        var domain = DomainWith("contoso.io", isMonitored: true, DateTimeOffset.UtcNow,
            ReportWith("google.com", Record(90, AuthResult.Pass, AuthResult.Pass), Record(10, AuthResult.Fail, AuthResult.Fail)));

        var (summary, rows) = DashboardSummary.Build([domain], parseFailureCount: 0);

        var row = Assert.Single(rows);
        Assert.Equal("Warning", row.Status);
        Assert.Equal(Color.Warning, row.StatusColor);
        Assert.Equal(1, summary.WarningCount);
    }

    [Fact]
    public void Build_MarksMonitoredDomainMissing_WhenNoReportEverReceived()
    {
        var domain = DomainWith("contoso.io", isMonitored: true, lastReportReceivedUtc: null);

        var (summary, rows) = DashboardSummary.Build([domain], parseFailureCount: 0);

        var row = Assert.Single(rows);
        Assert.Equal("Missing", row.Status);
        Assert.Equal(Color.Error, row.StatusColor);
        Assert.Equal(1, summary.MissingCount);
    }

    [Fact]
    public void Build_MarksMonitoredDomainMissing_WhenLastReportOlderThanTwoDays()
    {
        var domain = DomainWith("contoso.io", isMonitored: true, DateTimeOffset.UtcNow.AddDays(-3));

        var (_, rows) = DashboardSummary.Build([domain], parseFailureCount: 0);

        Assert.Equal("Missing", Assert.Single(rows).Status);
    }

    [Fact]
    public void Build_DoesNotMarkUnmonitoredDomainMissing_EvenWithNoReports()
    {
        var domain = DomainWith("contoso.io", isMonitored: false, lastReportReceivedUtc: null);

        var (summary, rows) = DashboardSummary.Build([domain], parseFailureCount: 0);

        Assert.Equal("OK", Assert.Single(rows).Status);
        Assert.Equal(0, summary.MissingCount);
    }

    [Fact]
    public void Build_CountsDistinctReportingOrgsAcrossAllDomains_AsSourceCount()
    {
        var domainA = DomainWith("contoso.io", isMonitored: true, DateTimeOffset.UtcNow,
            ReportWith("google.com", Record(10, AuthResult.Pass, AuthResult.Pass)),
            ReportWith("google.com", Record(5, AuthResult.Pass, AuthResult.Pass)));
        var domainB = DomainWith("fabrikam.com", isMonitored: true, DateTimeOffset.UtcNow,
            ReportWith("outlook.com", Record(10, AuthResult.Pass, AuthResult.Pass)));

        var (summary, _) = DashboardSummary.Build([domainA, domainB], parseFailureCount: 0);

        Assert.Equal(2, summary.SourceCount);
    }

    [Fact]
    public void Build_OrdersRowsBySortOrderThenName()
    {
        var domainA = new Domain { Name = "b.example.com", FirstSeenUtc = DateTimeOffset.UtcNow, SortOrder = 1 };
        var domainB = new Domain { Name = "a.example.com", FirstSeenUtc = DateTimeOffset.UtcNow, SortOrder = 0 };

        var (_, rows) = DashboardSummary.Build([domainA, domainB], parseFailureCount: 0);

        Assert.Equal(["a.example.com", "b.example.com"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Build_PassesThroughParseFailureCount()
    {
        var (summary, _) = DashboardSummary.Build([], parseFailureCount: 7);

        Assert.Equal(7, summary.ParseFailureCount);
    }

    [Fact]
    public void Build_ComputesOverallPassRate_VolumeWeightedAcrossDomains()
    {
        var domainA = DomainWith("contoso.io", isMonitored: true, DateTimeOffset.UtcNow,
            ReportWith("google.com", Record(10, AuthResult.Pass, AuthResult.Pass)));
        var domainB = DomainWith("fabrikam.com", isMonitored: true, DateTimeOffset.UtcNow,
            ReportWith("google.com", Record(990, AuthResult.Fail, AuthResult.Fail)));

        var (summary, _) = DashboardSummary.Build([domainA, domainB], parseFailureCount: 0);

        Assert.Equal(0.01, summary.OverallPassRate, precision: 3);
    }
}
