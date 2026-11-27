using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class DashboardActionCentreTests
{
    [Fact]
    public void CategorySelectionMatchesAny_WithoutDuplicatingDomainsOrTreatingUnknownAsDnsFailure()
    {
        var domain = Healthy();
        var missing = new DashboardAttention(domain, [new("Missing", "Investigate reports", "overview")], 0, null, true);
        var dns = new DashboardAttention(domain, [new("SPF missing", "Review configuration", "overview")], 2, null, false);
        var checking = new DashboardAttention(domain, [new("Unchecked", "Check configuration", "overview", true)], 0, null, false);
        Assert.False(missing.MatchesCategories("dns"));
        Assert.True(missing.MatchesCategories("dns,missing"));
        Assert.True(dns.MatchesCategories("dns,senders"));
        Assert.False(checking.MatchesCategories("dns,senders"));
        Assert.True(checking.MatchesCategories("checking"));
        Assert.Equal(2, new[] { missing, dns, checking }.Count(row => row.MatchesCategories("dns,missing,senders")));
        Assert.True(checking.MatchesCategories(null));
    }
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 16, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<int, int> NoReviews = new Dictionary<int, int>();
    private static Domain Healthy(int id = 1) => new()
    {
        Id = id, Name = $"domain-{id}.example", IsMonitored = true, LastReportReceivedUtc = Now,
        SpfCheckStatus = SpfCheckStatus.Ok, DmarcCheckStatus = DmarcCheckStatus.Ok,
        DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Ok, DmarcCheckedUtc = Now,
        DmarcPolicy = DmarcPolicyLevel.Reject, DmarcSubdomainPolicy = DmarcPolicyLevel.Reject
    };
    private static Report Report(DateTimeOffset received, int count, DispositionResult disposition) => new()
    {
        ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "",
        ReceivedUtc = received, DateRangeBeginUtc = received.AddDays(-1), DateRangeEndUtc = received,
        Records = [new() { SourceIp = "192.0.2.1", HeaderFrom = "domain.example", MessageCount = count, Disposition = disposition, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail }]
    };

    [Fact]
    public void RejectedSpoofingIsInvestigation_NotAConfigurationProblem()
    {
        var domain = Healthy();
        domain.Reports = [Report(Now.AddDays(-1), 400, DispositionResult.Reject)];
        var result = DashboardActionCentre.Build([domain], NoReviews, new(), Now);
        Assert.Empty(result.Attention);
        Assert.Equal(400, Assert.Single(result.Investigations).Rejected);
        Assert.Equal(0, result.PassRate);
        Assert.Equal(400, result.Rejected);
    }

    [Fact]
    public void ChartListAndDrilldownUseSameSevenUtcCalendarDays_AndMessageCounts()
    {
        var domain = Healthy();
        var start = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        domain.Reports =
        [
            Report(start.AddTicks(-1), 1000, DispositionResult.Reject),
            Report(start, 35, DispositionResult.Reject), Report(Now, 22, DispositionResult.Quarantine),
            Report(Now.AddTicks(1), 500, DispositionResult.Reject),
            Report(Now, 200, DispositionResult.None)
        ];
        var result = DashboardActionCentre.Build([domain], NoReviews, new(), Now);
        Assert.Equal(7, result.Days.Count);
        Assert.Equal(35, result.Days[0].Rejected);
        Assert.Equal(22, result.Days[^1].Quarantined);
        var row = Assert.Single(result.Investigations);
        Assert.Equal(57, row.Total);
        Assert.Equal(result.Quarantined + result.Rejected, result.Investigations.Sum(item => item.Total));
        Assert.Equal(3, row.Reports.Count);
        Assert.Equal(57, row.Reports.SelectMany(report => report.Records).Where(record => record.Disposition != DispositionResult.None).Sum(record => record.MessageCount));
    }

    [Fact]
    public void NullRoutedDomainDoesNotRequireReportsUsableDkimMtaStsOrDotMarcRua()
    {
        var domain = Healthy();
        domain.SpfCheckStatus = SpfCheckStatus.NullSpf;
        domain.LastReportReceivedUtc = null;
        domain.DkimSelectors = ["old"];
        domain.DkimCheckStatus = DkimCheckStatus.NoSendingKeys;
        domain.MtaStsEnabled = true;
        domain.MtaStsStatus = MtaStsStatus.Failed;
        domain.DmarcCheckStatus = DmarcCheckStatus.Misconfigured;
        domain.DmarcRecords = ["v=DMARC1; p=reject;"];
        domain.DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Missing;
        var result = DashboardActionCentre.Build([domain], NoReviews, new(), Now);
        Assert.Empty(result.Attention);
        Assert.Equal(0, result.MissingReports);
        Assert.Null(result.PassRate);
    }

    [Theory]
    [InlineData(DkimCheckStatus.UnexpectedSendingKey, false)]
    [InlineData(DkimCheckStatus.Missing, true)]
    public void NullDomainDkimProblemsAndUnverifiedAbsenceAreNotHidden(DkimCheckStatus status, bool checking)
    {
        var domain = Healthy();
        domain.SpfCheckStatus = SpfCheckStatus.NullSpf;
        domain.DkimSelectors = ["old"];
        domain.DkimCheckStatus = status;
        var row = Assert.Single(DashboardActionCentre.Build([domain], NoReviews, new(), Now).Attention);
        var problem = Assert.Single(row.Problems);
        Assert.Contains("DKIM", problem.Description);
        Assert.Equal(checking, problem.NeedsChecking);
    }

    [Fact]
    public void UncheckedEvidenceIsNotAllClear_AndOptInChecksStayOptional()
    {
        var healthy = Healthy();
        var unknown = new Domain { Id = 2, Name = "unknown.example" };
        var result = DashboardActionCentre.Build([healthy, unknown], NoReviews, new(), Now);
        var row = Assert.Single(result.Attention);
        Assert.Equal(2, row.Domain.Id);
        Assert.All(row.Problems, problem => Assert.True(problem.NeedsChecking));
        Assert.Equal(0, result.ProblemDomains);
        Assert.Equal(1, result.UncheckedDomains);
    }

    [Fact]
    public void ConfirmedProblemsSortBeforeSenderReviewAndUnknown_WithDeterministicTies()
    {
        var unknown = new Domain { Id = 1, Name = "aaa.example" };
        var review = Healthy(2);
        var missing = Healthy(3);
        missing.LastReportReceivedUtc = Now.AddDays(-3);
        var result = DashboardActionCentre.Build([unknown, review, missing], new Dictionary<int, int> { [2] = 4 }, new(), Now);
        Assert.Equal([3, 2, 1], result.Attention.Select(row => row.Domain.Id));
        Assert.Equal(4, result.SenderReviews);
        Assert.Equal(1, result.MissingReports);
        Assert.Equal(2, result.ProblemDomains);
    }

    [Fact]
    public void OnlySuppliedFilteredDomainsContribute_AndAffectsBothQueuesIndependently()
    {
        var domain = Healthy();
        domain.SpfCheckStatus = SpfCheckStatus.MissingRecord;
        domain.Reports = [Report(Now, 10, DispositionResult.Quarantine)];
        var counts = new Dictionary<int, int> { [1] = 3, [99] = 500 };
        var result = DashboardActionCentre.Build([domain], counts, new(), Now);
        Assert.Single(result.Attention);
        Assert.Single(result.Investigations);
        Assert.Equal(3, result.SenderReviews);
        Assert.Equal(1, result.DomainCount);
        Assert.Equal(10, result.Messages);
        Assert.Equal(1, result.Reports);
    }

    [Fact]
    public void ThirtyDayMetricsExcludeOldAndFutureReports_AndUseLongMessageTotals()
    {
        var domain = Healthy();
        domain.Reports =
        [
            Report(Now.AddDays(-31), 10, DispositionResult.None),
            Report(Now.AddDays(1), 10, DispositionResult.None),
            Report(Now.AddDays(-30), int.MaxValue, DispositionResult.None),
            Report(Now, int.MaxValue, DispositionResult.None)
        ];
        domain.Reports[2].Records[0].DkimResult = AuthResult.Pass;
        var result = DashboardActionCentre.Build([domain], NoReviews, new(), Now);
        Assert.Equal(2L * int.MaxValue, result.Messages);
        Assert.Equal(0.5, result.PassRate);
        Assert.Equal(2, result.Reports);
    }

    [Fact]
    public void ThirtyDayDataDoesNotImplySevenDayData()
    {
        var domain = Healthy();
        domain.Reports = [Report(Now.AddDays(-20), 30, DispositionResult.Reject)];
        var result = DashboardActionCentre.Build([domain], NoReviews, new(), Now);
        Assert.Equal(30, result.Messages);
        Assert.Equal(0, result.SevenDayMessages);
        Assert.Empty(result.Investigations);
    }

    [Fact]
    public void OptedInChecksShowConfirmedFailuresAndPendingEvidenceSeparately()
    {
        var domain = Healthy();
        domain.DkimSelectors = ["selector1"];
        domain.DkimCheckStatus = DkimCheckStatus.Missing;
        domain.MtaStsEnabled = true;
        domain.MtaStsStatus = MtaStsStatus.PendingCertificate;
        domain.TlsrptCheckStatus = TlsrptCheckStatus.NotChecked;
        var result = DashboardActionCentre.Build([domain], NoReviews, new(), Now);
        var row = Assert.Single(result.Attention);
        Assert.Single(row.Problems, problem => !problem.NeedsChecking);
        Assert.Equal(2, row.Problems.Count(problem => problem.NeedsChecking));
        Assert.Equal(1, result.ProblemDomains);
        Assert.Equal(1, result.UncheckedDomains);
    }

    [Fact]
    public void UnmonitoredValidAlternateReportingDestinationIsNotAFailure()
    {
        var domain = Healthy();
        domain.IsMonitored = false;
        domain.DmarcCheckStatus = DmarcCheckStatus.Misconfigured;
        domain.DmarcRecords = ["v=DMARC1; p=reject; rua=mailto:another@reports.example"];
        domain.LastReportReceivedUtc = null;
        var result = DashboardActionCentre.Build([domain], NoReviews, new(), Now);
        Assert.Empty(result.Attention);
    }
}
