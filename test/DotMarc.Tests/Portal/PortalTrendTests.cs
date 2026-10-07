using DotMarc.Data;
using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalTrendTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A report received on the given day with a passing record (SPF passes) and a failing one (both fail),
    /// which is the rule DomainStatistics.GetPassRate uses.</summary>
    private static Report ReportOn(DateTimeOffset receivedUtc, int passing, int failing) => new()
    {
        ReportingOrg = "google.com",
        ReportId = Guid.NewGuid().ToString(),
        RawXml = "<feedback/>",
        ReceivedUtc = receivedUtc,
        Records =
        [
            new ReportRecord { SourceIp = "192.0.2.1", HeaderFrom = "aurora-retail.example", MessageCount = passing, SpfResult = AuthResult.Pass, DkimResult = AuthResult.Fail },
            new ReportRecord { SourceIp = "198.51.100.7", HeaderFrom = "aurora-retail.example", MessageCount = failing, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail },
        ],
    };

    [Fact]
    public void EachDay_GetsItsOwnPassRate_OldestFirst_AndADayWithNoMailIsNull()
    {
        var reports = new[]
        {
            ReportOn(Now.AddDays(-2), passing: 9, failing: 1),
            ReportOn(Now, passing: 1, failing: 1),
        };

        var rates = PortalTrend.DailyPassRates(reports, days: 3, Now);

        Assert.Equal([0.9, null, 0.5], rates);
    }

    [Fact]
    public void ReportsOutsideTheDays_AreIgnored()
    {
        var rates = PortalTrend.DailyPassRates([ReportOn(Now.AddDays(-10), passing: 1, failing: 0)], days: 3, Now);

        Assert.Equal([null, null, null], rates);
    }
}
