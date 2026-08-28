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
