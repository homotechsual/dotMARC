using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.IpEnrichment;

[Collection("Postgres")]
public sealed class IpInfoServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public IpInfoServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private FakeDbContextFactory CreateDbContextFactory() => new(_connectionString);

    [Theory]
    [InlineData(null, true)] // never looked up
    [InlineData(IpLookupStatus.Ok, false)] // Ok is cached indefinitely, regardless of age
    public async Task NeedsLookup_ForFreshOrMissingRows(IpLookupStatus? status, bool expected)
    {
        var cached = status is { } s ? new IpInfo { Ip = "203.0.113.1", Status = s, LookedUpUtc = DateTimeOffset.UtcNow.AddDays(-365) } : null;

        Assert.Equal(expected, IpInfoService.NeedsLookup(cached, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(IpLookupStatus.NotFound, -25, true)]   // older than the 24h retry window
    [InlineData(IpLookupStatus.NotFound, -1, false)]   // within the retry window
    [InlineData(IpLookupStatus.LookupFailed, -25, true)]
    [InlineData(IpLookupStatus.LookupFailed, -1, false)]
    public void NeedsLookup_RetriesFailuresOnlyAfterTheRetryWindow(IpLookupStatus status, int ageHours, bool expected)
    {
        var cached = new IpInfo { Ip = "203.0.113.1", Status = status, LookedUpUtc = DateTimeOffset.UtcNow.AddHours(ageHours) };

        Assert.Equal(expected, IpInfoService.NeedsLookup(cached, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task GetCachedAsync_ReturnsOnlyTheRequestedIps()
    {
        await using var context = CreateContext();
        context.IpInfos.AddRange(
            new IpInfo { Ip = "203.0.113.1", Status = IpLookupStatus.Ok, Organization = "A", LookedUpUtc = DateTimeOffset.UtcNow },
            new IpInfo { Ip = "203.0.113.2", Status = IpLookupStatus.Ok, Organization = "B", LookedUpUtc = DateTimeOffset.UtcNow },
            new IpInfo { Ip = "203.0.113.3", Status = IpLookupStatus.Ok, Organization = "C", LookedUpUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var result = await IpInfoService.GetCachedAsync(context, ["203.0.113.1", "203.0.113.3", "198.51.100.1"], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result["203.0.113.1"].Organization);
        Assert.Equal("C", result["203.0.113.3"].Organization);
    }

    [Fact]
    public async Task EnrichAsync_InsertsANewRow_WhenNoneExists()
    {
        var lookup = new FakeIpInfoLookup { Result = new IpLookupResult(IpLookupStatus.Ok, "Example Org", "US") };

        var info = await IpInfoService.EnrichAsync(CreateDbContextFactory(), lookup, "203.0.113.5", CancellationToken.None);

        Assert.Equal("Example Org", info.Organization);
        Assert.Equal("US", info.Country);

        await using var verify = CreateContext();
        var stored = await verify.IpInfos.SingleAsync(i => i.Ip == "203.0.113.5");
        Assert.Equal(IpLookupStatus.Ok, stored.Status);
    }

    [Fact]
    public async Task EnrichAsync_UpdatesAnExistingStaleRow_RatherThanDuplicatingIt()
    {
        await using (var context = CreateContext())
        {
            context.IpInfos.Add(new IpInfo
            {
                Ip = "203.0.113.6",
                Status = IpLookupStatus.LookupFailed,
                LookedUpUtc = DateTimeOffset.UtcNow.AddDays(-2)
            });
            await context.SaveChangesAsync();
        }

        var lookup = new FakeIpInfoLookup { Result = new IpLookupResult(IpLookupStatus.Ok, "Now Resolved Inc", "CA") };
        await IpInfoService.EnrichAsync(CreateDbContextFactory(), lookup, "203.0.113.6", CancellationToken.None);

        await using var verify = CreateContext();
        Assert.Single(verify.IpInfos.Where(i => i.Ip == "203.0.113.6"));
        var stored = await verify.IpInfos.SingleAsync(i => i.Ip == "203.0.113.6");
        Assert.Equal(IpLookupStatus.Ok, stored.Status);
        Assert.Equal("Now Resolved Inc", stored.Organization);
    }

    [Fact]
    public async Task EnrichAsync_UpsertsAnIpRange_WhenTheResultIncludesRangeBounds()
    {
        var lookup = new FakeIpInfoLookup
        {
            Result = new IpLookupResult(IpLookupStatus.Ok, "Microsoft Limited", "GB", "2a01:110::", "2a01:111:ffff:ffff:ffff:ffff:ffff:ffff")
        };

        await IpInfoService.EnrichAsync(CreateDbContextFactory(), lookup, "2a01:111:f403:c207::3", CancellationToken.None);

        await using var verify = CreateContext();
        var stored = await verify.IpRanges.SingleAsync(r => r.RangeStart == "2a01:110::" && r.RangeEnd == "2a01:111:ffff:ffff:ffff:ffff:ffff:ffff");
        Assert.Equal("Microsoft Limited", stored.Organization);
        Assert.Equal("GB", stored.Country);
    }

    [Fact]
    public async Task EnrichAsync_DoesNotUpsertARange_WhenTheResultHasNoRangeBounds()
    {
        var lookup = new FakeIpInfoLookup { Result = new IpLookupResult(IpLookupStatus.Ok, "Example Org", "US") };

        await IpInfoService.EnrichAsync(CreateDbContextFactory(), lookup, "203.0.113.7", CancellationToken.None);

        await using var verify = CreateContext();
        Assert.Empty(verify.IpRanges);
    }

    [Fact]
    public async Task EnrichAsync_HandlesConcurrentFirstLookups_ForTheSameRange_WithoutThrowing()
    {
        var dbFactory = CreateDbContextFactory();
        var lookupA = new FakeIpInfoLookup { Result = new IpLookupResult(IpLookupStatus.Ok, "Microsoft Limited", "GB", "2a01:110::", "2a01:111:ffff:ffff:ffff:ffff:ffff:ffff") };
        var lookupB = new FakeIpInfoLookup { Result = new IpLookupResult(IpLookupStatus.Ok, "Microsoft Limited", "GB", "2a01:110::", "2a01:111:ffff:ffff:ffff:ffff:ffff:ffff") };

        await Task.WhenAll(
            IpInfoService.EnrichAsync(dbFactory, lookupA, "2a01:111:f403:c200::1", CancellationToken.None),
            IpInfoService.EnrichAsync(dbFactory, lookupB, "2a01:111:f403:c201::1", CancellationToken.None));

        await using var verify = CreateContext();
        Assert.Single(verify.IpRanges);
    }

    [Fact]
    public async Task GetCachedRangesAsync_ReturnsAllStoredRanges()
    {
        await using (var context = CreateContext())
        {
            context.IpRanges.Add(new IpRange { RangeStart = "203.0.113.0", RangeEnd = "203.0.113.255", Organization = "Example Org", LookedUpUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var ranges = await IpInfoService.GetCachedRangesAsync(verify, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal("Example Org", ranges[0].Organization);
    }

    [Fact]
    public async Task EnrichAsync_HandlesConcurrentFirstLookups_ForTheSameIp_WithoutThrowing()
    {
        var dbFactory = CreateDbContextFactory();
        var lookupA = new FakeIpInfoLookup { Result = new IpLookupResult(IpLookupStatus.Ok, "Example Org", "US") };
        var lookupB = new FakeIpInfoLookup { Result = new IpLookupResult(IpLookupStatus.Ok, "Example Org", "US") };

        var results = await Task.WhenAll(
            IpInfoService.EnrichAsync(dbFactory, lookupA, "203.0.113.99", CancellationToken.None),
            IpInfoService.EnrichAsync(dbFactory, lookupB, "203.0.113.99", CancellationToken.None));

        Assert.All(results, r => Assert.Equal("Example Org", r.Organization));

        await using var verify = CreateContext();
        Assert.Single(verify.IpInfos.Where(i => i.Ip == "203.0.113.99"));
    }
}
