using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class SpfCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SpfCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task RunSpfCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeSpfDnsChecker { Result = new(SpfCheckStatus.MissingRecord, "No SPF record") };
        var service = CreateService(context);
        await service.RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Contains("contoso.io", checker.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(SpfCheckStatus.MissingRecord, domain.SpfCheckStatus);
        Assert.NotNull(domain.SpfCheckedUtc);
    }

    [Fact]
    public async Task RunSpfCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            SpfCheckStatus = SpfCheckStatus.Ok,
            SpfCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var checker = new FakeSpfDnsChecker();
        var service = CreateService(context);
        await service.RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().SpfCheckedUtc);
    }

    [Fact]
    public async Task RunSpfCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            SpfCheckStatus = SpfCheckStatus.MissingRecord
        });
        await context.SaveChangesAsync();

        var checker = new FakeSpfDnsChecker { ShouldThrow = true };
        var service = CreateService(context);
        await service.RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(SpfCheckStatus.MissingRecord, verifyDomain.SpfCheckStatus);
        Assert.Null(verifyDomain.SpfCheckedUtc);
    }

    [Fact]
    public async Task RunSpfCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.SpfCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checker = new FakeSpfDnsChecker();
        var service = CreateService(context);
        await service.RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);

        await lockTransaction.RollbackAsync();
    }

    private static readonly SpfServiceRange[] SampleRanges = [new("Mailchimp", "servers.mcsv.net", "198.51.100.0/24")];

    [Fact]
    public async Task RunSingleSpfCheckAsync_StoresTheRangesTheCheckFound()
    {
        var domain = new Domain { Name = "contoso.io" };
        var checker = new FakeSpfDnsChecker { Result = new(SpfCheckStatus.Ok, null, SampleRanges) };

        await PollingService.RunSingleSpfCheckAsync(domain, checker, CancellationToken.None);

        Assert.Equal(SampleRanges, SpfServiceRanges.Parse(domain.SpfRangesJson));
    }

    [Fact]
    public async Task RunSingleSpfCheckAsync_KeepsTheLastGoodRanges_WhenTheCheckFoundNone()
    {
        var domain = new Domain { Name = "contoso.io", SpfRangesJson = SpfServiceRanges.Serialize(SampleRanges) };
        var checker = new FakeSpfDnsChecker { Result = new(SpfCheckStatus.Ok, null, null) };

        await PollingService.RunSingleSpfCheckAsync(domain, checker, CancellationToken.None);

        Assert.Equal(SampleRanges, SpfServiceRanges.Parse(domain.SpfRangesJson));
    }

    [Fact]
    public async Task RunSingleSpfCheckAsync_ClearsTheRanges_WhenTheDomainAuthorisesNothing()
    {
        var domain = new Domain { Name = "contoso.io", SpfRangesJson = SpfServiceRanges.Serialize(SampleRanges) };
        var checker = new FakeSpfDnsChecker { Result = new(SpfCheckStatus.NullSpf, "none", []) };

        await PollingService.RunSingleSpfCheckAsync(domain, checker, CancellationToken.None);

        Assert.Equal("[]", domain.SpfRangesJson);
    }

    [Fact]
    public async Task TheStoredRanges_SurviveASaveAndReload()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, SpfRangesJson = SpfServiceRanges.Serialize(SampleRanges) });
        await context.SaveChangesAsync();

        using var reload = CreateContext();
        Assert.Equal(SampleRanges, SpfServiceRanges.Parse(reload.Domains.Single().SpfRangesJson));
    }
}
