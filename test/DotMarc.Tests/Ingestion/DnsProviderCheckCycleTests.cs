using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class DnsProviderCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsProviderCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task RunDnsProviderCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "services.wrc.wales", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var detector = new FakeDnsProviderDetector { Result = new(DetectedDnsProvider.AzureDns, "wrc.wales") };
        var service = CreateService(context);
        await service.RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        Assert.Contains("services.wrc.wales", detector.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(DetectedDnsProvider.AzureDns, domain.DnsProvider);
        Assert.Equal("wrc.wales", domain.DnsZone);
        Assert.NotNull(domain.DnsProviderCheckedUtc);
    }

    [Fact]
    public async Task RunDnsProviderCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DnsProvider = DetectedDnsProvider.Cloudflare,
            DnsZone = "contoso.io",
            DnsProviderCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var detector = new FakeDnsProviderDetector();
        var service = CreateService(context);
        await service.RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        Assert.Empty(detector.CheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().DnsProviderCheckedUtc);
    }

    [Fact]
    public async Task RunDnsProviderCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DnsProvider = DetectedDnsProvider.Unknown
        });
        await context.SaveChangesAsync();

        var detector = new FakeDnsProviderDetector { ShouldThrow = true };
        var service = CreateService(context);
        await service.RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(DetectedDnsProvider.Unknown, verifyDomain.DnsProvider);
        Assert.Null(verifyDomain.DnsProviderCheckedUtc);
    }

    [Fact]
    public async Task RunDnsProviderCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.DnsProviderCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var detector = new FakeDnsProviderDetector();
        var service = CreateService(context);
        await service.RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        Assert.Empty(detector.CheckedDomains);

        await lockTransaction.RollbackAsync();
    }
}
