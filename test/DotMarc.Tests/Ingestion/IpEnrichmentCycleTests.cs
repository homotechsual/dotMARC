using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class IpEnrichmentCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public IpEnrichmentCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static PollingService CreateService(DotMarcDbContext context) =>
        new(new FakeGraphMailboxClient(), context, NullLogger<PollingService>.Instance);

    private async Task SeedReportRecordAsync(string sourceIp)
    {
        await using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = $"domain-for-{sourceIp}.test",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            Reports =
            {
                new Report
                {
                    ReportingOrg = "google.com",
                    ReportId = Guid.NewGuid().ToString(),
                    DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    DateRangeEndUtc = DateTimeOffset.UtcNow,
                    RawXml = "<feedback/>",
                    ReceivedUtc = DateTimeOffset.UtcNow,
                    AuthDetailBackfilledUtc = DateTimeOffset.UtcNow,
                    Records = { new ReportRecord { SourceIp = sourceIp, MessageCount = 1, HeaderFrom = "contoso.io" } }
                }
            }
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_EnrichesASourceIpNeverLookedUpBefore()
    {
        await SeedReportRecordAsync("203.0.113.10");

        using var context = CreateContext();
        var lookup = new FakeIpInfoLookup { Result = new(IpLookupStatus.Ok, "Example Org", "US") };
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        Assert.Contains("203.0.113.10", lookup.LookedUpIps);

        using var verify = CreateContext();
        var info = verify.IpInfos.Single(i => i.Ip == "203.0.113.10");
        Assert.Equal("Example Org", info.Organization);
        Assert.Equal(IpLookupStatus.Ok, info.Status);
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_SkipsAnIpWithARecentSuccessfulLookup()
    {
        await SeedReportRecordAsync("203.0.113.11");
        using (var seed = CreateContext())
        {
            seed.IpInfos.Add(new IpInfo { Ip = "203.0.113.11", Organization = "Cached Org", Status = IpLookupStatus.Ok, LookedUpUtc = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext();
        var lookup = new FakeIpInfoLookup();
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        Assert.Empty(lookup.LookedUpIps);
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_RetriesAnIpWhoseFailedLookupIsOlderThanTheRetryWindow()
    {
        await SeedReportRecordAsync("203.0.113.12");
        using (var seed = CreateContext())
        {
            seed.IpInfos.Add(new IpInfo { Ip = "203.0.113.12", Status = IpLookupStatus.LookupFailed, LookedUpUtc = DateTimeOffset.UtcNow.AddHours(-25) });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext();
        var lookup = new FakeIpInfoLookup { Result = new(IpLookupStatus.Ok, "Now Reachable", "GB") };
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        Assert.Contains("203.0.113.12", lookup.LookedUpIps);
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_ContinuesPastOneFailingLookup()
    {
        await SeedReportRecordAsync("203.0.113.13");
        await SeedReportRecordAsync("203.0.113.14");

        using var context = CreateContext();
        var lookup = new FakeIpInfoLookup { Result = new(IpLookupStatus.Ok, "Example Org", "US") };
        lookup.IpsToThrowFor.Add("203.0.113.13");
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        using var verify = CreateContext();
        Assert.Null(verify.IpInfos.SingleOrDefault(i => i.Ip == "203.0.113.13"));
        Assert.NotNull(verify.IpInfos.SingleOrDefault(i => i.Ip == "203.0.113.14"));
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        await SeedReportRecordAsync("203.0.113.15");

        using var context = CreateContext();
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.IpEnrichmentLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var lookup = new FakeIpInfoLookup();
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        Assert.Empty(lookup.LookedUpIps);

        await lockTransaction.RollbackAsync();
    }
}
