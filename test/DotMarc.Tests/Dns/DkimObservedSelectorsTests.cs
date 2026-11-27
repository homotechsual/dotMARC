using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Dns;

[Collection("Postgres")]
public sealed class DkimObservedSelectorsTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task SuggestionsUseExactSigningDomainAndDistinctRecordMessagesWithoutChangingManualSettings()
    {
        var (connection, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using var db = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connection).Options);
            await db.Database.MigrateAsync();
            var firstSeen = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var latest = firstSeen.AddDays(2);
            var domain = new Domain { Name = "example.com", DkimSelectors = ["manual"], FirstSeenUtc = firstSeen };
            var other = new Domain { Name = "other.example", FirstSeenUtc = firstSeen };
            db.Domains.AddRange(domain, other);
            db.Reports.AddRange(
                ReportFor(domain, "first", firstSeen, 5,
                    Dkim("example.com", "rotate"), Dkim("EXAMPLE.COM.", " ROTATE ", DmarcMechanismResult.Fail),
                    Dkim("example.com", "old", DmarcMechanismResult.Fail),
                    Dkim("sub.example.com", "child"), Dkim("notexample.com", "third-party"),
                    Dkim("example.com.evil.example", "suffix"),
                    new() { Domain = "example.com", Mechanism = DmarcAuthMechanism.Spf, Result = DmarcMechanismResult.Pass, Selector = "spf" }),
                ReportFor(domain, "second", latest, 7,
                    Dkim(" example.com. ", "rotate"), Dkim("example.com", "s.2026"),
                    Dkim("example.com", null), Dkim("example.com", " "), Dkim("example.com", "bad/name"),
                    Dkim("example.com", "-bad"), Dkim("example.com", new string('s', 64))),
                ReportFor(other, "unrelated-report", latest.AddDays(1), 200, Dkim("example.com", "unrelated")));
            await db.SaveChangesAsync();

            var observed = await DkimObservedSelectors.GetAsync(db, domain.Id);

            Assert.Equal(
                [new DkimObservedSelector("rotate", 12, latest), new("s.2026", 7, latest), new("old", 5, firstSeen)],
                observed);
            await db.Entry(domain).ReloadAsync();
            Assert.Equal(["manual"], domain.DkimSelectors);
            Assert.Empty(await DkimObservedSelectors.GetAsync(db, other.Id));
        }
    }

    [Fact]
    public async Task ANewDomainHasNoSuggestionsAndDoesNotGainProviderDefaults()
    {
        var (connection, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using var db = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connection).Options);
            await db.Database.MigrateAsync();
            var domain = new Domain { Name = "empty.example", FirstSeenUtc = DateTimeOffset.UtcNow };
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            Assert.Empty(await DkimObservedSelectors.GetAsync(db, domain.Id));
            Assert.Empty(domain.DkimSelectors);
        }
    }

    private static ReportRecordAuthDetail Dkim(string domain, string? selector, DmarcMechanismResult result = DmarcMechanismResult.Pass) =>
        new() { Domain = domain, Selector = selector, Mechanism = DmarcAuthMechanism.Dkim, Result = result };

    private static Report ReportFor(Domain domain, string id, DateTimeOffset periodEnd, int count, params ReportRecordAuthDetail[] details) =>
        new()
        {
            Domain = domain, ReportingOrg = "receiver.example", ReportId = id,
            DateRangeBeginUtc = periodEnd.AddDays(-1), DateRangeEndUtc = periodEnd,
            ReceivedUtc = periodEnd.AddDays(5), RawXml = "<feedback/>",
            Records = [new() { SourceIp = "192.0.2.1", HeaderFrom = domain.Name, MessageCount = count, AuthDetails = [.. details] }]
        };
}
