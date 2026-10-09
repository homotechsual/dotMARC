using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using DotMarc.Reporting;
using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ClientReportCalculatorTests
{
    private static readonly ReportPeriod March = new(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);
    private static readonly ResolvedBrand Brand = new("Nova MSP", "Aurora Retail Ltd", "#0B5FFF", "#FF6B00", null, null, null, null, null, null);
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 6, 0, 0, TimeSpan.Zero);

    private static Domain AuroraDomain(int id = 1, string name = "aurora-retail.example") => new()
    {
        Id = id, Name = name, IsMonitored = true, DmarcPolicy = DmarcPolicyLevel.Reject, LastReportReceivedUtc = Now,
        DmarcCheckStatus = DmarcCheckStatus.Ok, SpfCheckStatus = SpfCheckStatus.Ok, MxCheckStatus = MxCheckStatus.Ok,
        DkimCheckStatus = DkimCheckStatus.Ok, TlsrptCheckStatus = TlsrptCheckStatus.Ok,
    };

    private static Report ReportOn(int domainId, DateTimeOffset beginUtc, params ReportRecord[] records) => new()
    {
        DomainId = domainId, ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = beginUtc,
        DateRangeEndUtc = beginUtc.AddDays(1), ReceivedUtc = beginUtc.AddDays(1), Records = records.ToList(), RawXml = "<feedback/>",
    };

    private static ReportRecord Record(string ip, int count, AuthResult spf, AuthResult dkim, DispositionResult disposition = DispositionResult.None) => new()
    {
        SourceIp = ip, MessageCount = count, SpfResult = spf, DkimResult = dkim, Disposition = disposition, HeaderFrom = "aurora-retail.example",
    };

    private static ClientReport Build(IReadOnlyList<Domain> domains, IReadOnlyList<Report> reports, ReportPeriod? period = null, TimeZoneInfo? zone = null,
        IReadOnlyList<AlertEvent>? periodAlerts = null, IReadOnlyDictionary<string, IpInfo>? owners = null) =>
        ClientReportCalculator.Build(new ClientReportInputs(Brand, null, "Aurora Retail", period ?? March, zone ?? TimeZoneInfo.Utc, domains, reports,
            periodAlerts ?? [], [], owners ?? new Dictionary<string, IpInfo>(), Now));

    [Fact]
    public void ThePassRate_MatchesTheDashboardsDefinition()
    {
        var domain = AuroraDomain();
        var reports = new[]
        {
            ReportOn(1, new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                Record("203.0.113.10", 90, AuthResult.Pass, AuthResult.Fail), Record("198.51.100.7", 10, AuthResult.Fail, AuthResult.Fail)),
        };

        var report = Build([domain], reports);

        var row = Assert.Single(report.Domains);
        Assert.Equal((100L, DomainStatistics.GetPassRate(reports)), (row.Messages, row.PassRate));
    }

    [Fact]
    public void TheChange_IsAgainstThePreviousMonth_OrNewWhenThereWasNothingBefore()
    {
        var domain = AuroraDomain();
        var february = ReportOn(1, new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero), Record("203.0.113.10", 80, AuthResult.Pass, AuthResult.Pass), Record("198.51.100.7", 20, AuthResult.Fail, AuthResult.Fail));
        var march = ReportOn(1, new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), Record("203.0.113.10", 90, AuthResult.Pass, AuthResult.Pass), Record("198.51.100.7", 10, AuthResult.Fail, AuthResult.Fail));

        Assert.Equal("+10.0 pts", Build([domain], [february, march]).Domains.Single().ChangeText);
        Assert.Equal("new", Build([domain], [march]).Domains.Single().ChangeText);
    }

    [Fact]
    public void AReportStraddlingLocalMidnight_CountsInTheLocalDaysPeriod()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var domain = AuroraDomain();
        // 23:30 UTC on 28 February is 00:30 on 1 March in Berlin.
        var lateFebruaryUtc = ReportOn(1, new DateTimeOffset(2026, 2, 28, 23, 30, 0, TimeSpan.Zero), Record("203.0.113.10", 50, AuthResult.Pass, AuthResult.Pass));

        Assert.Equal(50L, Build([domain], [lateFebruaryUtc], zone: berlin).Domains.Single().Messages);
        Assert.Equal(0L, Build([domain], [lateFebruaryUtc], zone: TimeZoneInfo.Utc).Domains.Single().Messages);
    }

    [Fact]
    public void TopSenders_AreByVolume_WithTheirOwnersAndPassAndFailCounts()
    {
        var domain = AuroraDomain();
        var reports = new[]
        {
            ReportOn(1, new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                Record("203.0.113.10", 70, AuthResult.Pass, AuthResult.Pass),
                Record("198.51.100.7", 25, AuthResult.Fail, AuthResult.Fail),
                Record("198.51.100.7", 5, AuthResult.Pass, AuthResult.Fail)),
        };
        var owners = new Dictionary<string, IpInfo> { ["198.51.100.7"] = new() { Ip = "198.51.100.7", Organization = "Mailchimp" } };

        var senders = Build([domain], reports, owners: owners).Domains.Single().TopSenders;

        Assert.Equal(new ClientReportSender("203.0.113.10", null, 70, 70, 0, 0.7), senders[0]);
        Assert.Equal(new ClientReportSender("198.51.100.7", "Mailchimp", 30, 5, 25, 0.3), senders[1]);
    }

    [Fact]
    public void ReceiverActions_CountWhatHappened_IncludingFailingMailDelivered()
    {
        var domain = AuroraDomain();
        var reports = new[]
        {
            ReportOn(1, new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                Record("203.0.113.10", 60, AuthResult.Pass, AuthResult.Pass),
                Record("198.51.100.7", 15, AuthResult.Fail, AuthResult.Fail),
                Record("198.51.100.8", 20, AuthResult.Fail, AuthResult.Fail, DispositionResult.Quarantine),
                Record("198.51.100.9", 5, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject)),
        };

        Assert.Equal(new ReceiverActions(75, 20, 5, 15), Build([domain], reports).Domains.Single().Receivers);
    }

    [Fact]
    public void TheTrend_HasAPointPerLocalDay_WithGapsWhereThereWasNoData()
    {
        var domain = AuroraDomain();
        var reports = new[] { ReportOn(1, new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), Record("203.0.113.10", 10, AuthResult.Pass, AuthResult.Pass)) };

        var trend = Build([domain], reports).Domains.Single().Trend;

        Assert.Equal(31, trend.Count);
        Assert.Null(trend[0]);
        Assert.Equal(1.0, trend[1]);
    }

    [Fact]
    public void ALongRange_HasWeeklyTrendPoints()
    {
        var domain = AuroraDomain();
        var halfYear = new ReportPeriod(new DateOnly(2025, 10, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Custom);

        Assert.Equal(26, Build([domain], [], period: halfYear).Domains.Single().Trend.Count); // 182 days in 7-day buckets, rounded up
    }

    [Fact]
    public void Alerts_RaisedOrResolvedInThePeriod_AreListed()
    {
        var domain = AuroraDomain();
        var alerts = new[]
        {
            new AlertEvent { DomainName = "aurora-retail.example", AlertType = AlertTypes.SpfRecordBroken, Severity = "Warning", Title = "SPF record broken", Message = "", CreatedUtc = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero) },
        };

        Assert.Equal("SPF record broken", Build([domain], [], periodAlerts: alerts).Domains.Single().Alerts.Single().Title);
    }

    [Fact]
    public void TheVerdict_IsThePortals()
    {
        var report = Build([AuroraDomain(), AuroraDomain(2, "shop.aurora-retail.example")], []);

        Assert.Equal(PortalStatus.Verdict(report.Domains.Select(domain => domain.Status).ToList()), report.Verdict);
    }
}
