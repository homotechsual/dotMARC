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
public sealed class DmarcCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DmarcCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task RunDmarcCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { Result = new DmarcCheckResult(DmarcCheckStatus.MissingOwnRecord, "No TXT record found at _dmarc.contoso.io") };
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Contains("contoso.io", checker.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(DmarcCheckStatus.MissingOwnRecord, domain.DmarcCheckStatus);
        Assert.Equal("No TXT record found at _dmarc.contoso.io", domain.DmarcCheckDetail);
        Assert.NotNull(domain.DmarcCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok,
            DmarcCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker();
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().DmarcCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcCheckCycleAsync_RechecksADomainCheckedMoreThan24HoursAgo()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.MissingOwnRecord,
            DmarcCheckedUtc = DateTimeOffset.UtcNow.AddHours(-25)
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { Result = new DmarcCheckResult(DmarcCheckStatus.Ok, null) };
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Contains("contoso.io", checker.CheckedDomains);
        Assert.Equal(DmarcCheckStatus.Ok, context.Domains.Single().DmarcCheckStatus);
    }

    [Fact]
    public async Task RunDmarcCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.MissingOwnRecord,
            DmarcCheckDetail = "No TXT record found at _dmarc.contoso.io"
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { ShouldThrow = true };
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(DmarcCheckStatus.MissingOwnRecord, verifyDomain.DmarcCheckStatus);
        Assert.Equal("No TXT record found at _dmarc.contoso.io", verifyDomain.DmarcCheckDetail);
        Assert.Null(verifyDomain.DmarcCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.DmarcCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checker = new FakeDmarcDnsChecker();
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);

        await lockTransaction.RollbackAsync();
    }
}
