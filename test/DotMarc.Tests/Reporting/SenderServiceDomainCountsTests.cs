using DotMarc.Data;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting;

/// <summary>How many domains show each sending service, for the baseline and group defaults screens: found where the evidence is strong,
/// likely where the service is attributed on weaker evidence.</summary>
[Collection("Postgres")]
public sealed class SenderServiceDomainCountsTests(PostgresContainerFixture fixture)
{
    private static SenderServiceRow Row(string name, double evidence) =>
        new(name, IsUnrecognised: false, Messages: 10, Share: 0.1, Evidence: evidence, PassRate: 1, Passing: 10, Failing: 0, ActedOn: 0,
            DominantCause: null, Fix: null, EvidenceLines: [], SourceIps: []);

    private static SenderIdentificationResult Result(params SenderServiceRow[] services) => new(services, Unrecognised: null, new Dictionary<string, string>());

    [Fact]
    public void ADomainCountsOncePerService_AsFoundOrLikelyByItsEvidence()
    {
        var counts = SenderServiceDomainCounts.Count(
        [
            Result(Row("Mailchimp", 0.90), Row("SendGrid", 0.50)),
            Result(Row("Mailchimp", 0.60)),
            Result(Row("Mailchimp", 0.85)),
        ]);

        Assert.Equal(new ServiceDomainCount(Found: 2, Likely: 1), counts["Mailchimp"]);
        Assert.Equal(new ServiceDomainCount(Found: 0, Likely: 1), counts["SendGrid"]);
    }

    [Theory]
    [InlineData(0.70, 1, 0)]
    [InlineData(0.699, 0, 1)]
    [InlineData(0.30, 0, 1)]
    public void TheStrongBoundary_IsExactlyTheSourcesTablesStrongBand(double evidence, int found, int likely)
    {
        var counts = SenderServiceDomainCounts.Count([Result(Row("Zoho", evidence))]);

        Assert.Equal(new ServiceDomainCount(found, likely), counts["Zoho"]);
    }

    [Fact]
    public void AServiceNoDomainShows_IsAbsent_AndTheUnrecognisedRowIsNeverCounted()
    {
        var unrecognised = new SenderServiceRow(SenderIdentification.UnrecognisedName, IsUnrecognised: true, 5, 0.1, null, 1, 5, 0, 0, null, null, [], ["203.0.113.9"]);

        var counts = SenderServiceDomainCounts.Count([new SenderIdentificationResult([], unrecognised, new Dictionary<string, string>())]);

        Assert.Empty(counts);
    }

    private static ReportRecord Mail(string domainName, string ip, string authDomain, string? selector = null)
    {
        var record = new ReportRecord
        {
            SourceIp = ip, MessageCount = 40, Disposition = DispositionResult.None, HeaderFrom = domainName, SpfResult = AuthResult.Pass, DkimResult = AuthResult.Pass
        };
        record.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = authDomain, Selector = selector, Result = DmarcMechanismResult.Pass });
        return record;
    }

    private static async Task<int> AddDomainAsync(DotMarcDbContext context, string name, bool monitored, string? groupName, params ReportRecord[] records)
    {
        var received = DateTimeOffset.UtcNow.AddDays(-2);
        var domain = new Domain
        {
            Name = name, IsMonitored = monitored, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-60), LastReportReceivedUtc = received,
            Groups = groupName is null ? [] : [await context.Groups.FirstOrDefaultAsync(group => group.Name == groupName) ?? new Group { Name = groupName }],
            Reports =
            [
                new Report
                {
                    ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>", DateRangeBeginUtc = received.AddDays(-1),
                    DateRangeEndUtc = received, ReceivedUtc = received, AuthDetailBackfilledUtc = received, Records = [.. records]
                }
            ]
        };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    [Fact]
    public async Task LoadAsync_CountsMonitoredDomains_AndFilteringByGroupNarrowsThem()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);
            await context.Database.MigrateAsync();
            // Strong: the sender signs as its own domain. Likely: only its DKIM selector matches, under the customer's own domain.
            await AddDomainAsync(context, "a.test", monitored: true, "Aurora", Mail("a.test", "198.51.100.7", "bounce.mcsv.net"), Mail("a.test", "198.51.100.20", "bounce.sendgrid.net"));
            await AddDomainAsync(context, "b.test", monitored: true, "Other", Mail("b.test", "198.51.100.8", "b.test", selector: "k1"));
            await AddDomainAsync(context, "c.test", monitored: false, "Aurora", Mail("c.test", "198.51.100.9", "bounce.mcsv.net"));
            var auroraGroupId = await context.Groups.Where(group => group.Name == "Aurora").Select(group => group.Id).SingleAsync();

            var everyone = await SenderServiceDomainCounts.LoadAsync(context, groupId: null, CancellationToken.None);
            var aurora = await SenderServiceDomainCounts.LoadAsync(context, auroraGroupId, CancellationToken.None);

            Assert.Equal(new ServiceDomainCount(Found: 1, Likely: 1), everyone["Mailchimp"]);
            Assert.Equal(new ServiceDomainCount(Found: 1, Likely: 0), everyone["SendGrid"]);
            Assert.Equal(new ServiceDomainCount(Found: 1, Likely: 0), aurora["Mailchimp"]);
            Assert.False(aurora.ContainsKey("Microsoft 365"));
        }
    }

    [Fact]
    public async Task LoadAsync_IgnoresReportsOlderThanTheWindow()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);
            await context.Database.MigrateAsync();
            var domainId = await AddDomainAsync(context, "old.test", monitored: true, null, Mail("old.test", "198.51.100.7", "bounce.mcsv.net"));
            var report = await context.Reports.SingleAsync(candidate => candidate.DomainId == domainId);
            report.ReceivedUtc = DateTimeOffset.UtcNow.AddDays(-45);
            await context.SaveChangesAsync();

            var counts = await SenderServiceDomainCounts.LoadAsync(context, groupId: null, CancellationToken.None);

            Assert.Empty(counts);
        }
    }
}
