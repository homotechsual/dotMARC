using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.DnsPush;

[Collection("Postgres")]
public sealed class DnsProviderCheckServiceTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task ChecksPersistImmediatelyAndRespectCurrentScopeWithoutErasingFailedDetection()
    {
        var (connection, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            var factory = new FakeDbContextFactory(connection);
            await using var db = factory.CreateDbContext();
            await db.Database.MigrateAsync();
            var group = new Group { Name = "Allowed" };
            var first = new Domain { Name = "first.example", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] };
            var second = new Domain { Name = "second.example", FirstSeenUtc = DateTimeOffset.UtcNow };
            db.Domains.AddRange(first, second);
            await db.SaveChangesAsync();
            var detector = new ScriptedDnsProviderDetector();
            var service = new DnsProviderCheckService(factory, detector, NullLogger<DnsProviderCheckService>.Instance);

            Assert.True((await service.CheckAsync(first.Id, [group.Id])).Succeeded);
            await db.Entry(first).ReloadAsync();
            Assert.Equal(DetectedDnsProvider.Cloudflare, first.DnsProvider);
            Assert.Equal("first.example", first.DnsZone);
            Assert.Equal(["ns1.example"], first.DnsNameservers);
            Assert.NotNull(first.DnsProviderCheckedUtc);
            var checkedAt = first.DnsProviderCheckedUtc;

            detector.Failing.Add(first.Name);
            Assert.False((await service.CheckAsync(first.Id, [])).Succeeded);
            await db.Entry(first).ReloadAsync();
            Assert.Equal(checkedAt, first.DnsProviderCheckedUtc);
            Assert.Equal(DetectedDnsProvider.Cloudflare, first.DnsProvider);
            Assert.False((await service.CheckAsync(second.Id, [group.Id])).Succeeded);
            await db.Entry(second).ReloadAsync();
            Assert.Null(second.DnsProviderCheckedUtc);
            Assert.False((await service.CheckAsync(int.MaxValue, [])).Succeeded);
            Assert.True((await service.CheckAsync(second.Id, [])).Succeeded);

            first.Groups.Clear();
            await db.SaveChangesAsync();
            Assert.False((await service.CheckAsync(first.Id, [group.Id])).Succeeded);
        }
    }
}
