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
public sealed class DmarcAuthorizationCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DmarcAuthorizationCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task RunDmarcAuthorizationCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { AuthorizationResult = new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.Missing, "No TXT record found") };
        var service = CreateService(context);
        await service.RunDmarcAuthorizationCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Contains("contoso.io", checker.AuthorizationCheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(DmarcAuthorizationCheckStatus.Missing, domain.DmarcAuthorizationCheckStatus);
        Assert.NotNull(domain.DmarcAuthorizationCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcAuthorizationCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Ok,
            DmarcAuthorizationCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker();
        var service = CreateService(context);
        await service.RunDmarcAuthorizationCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Empty(checker.AuthorizationCheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().DmarcAuthorizationCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcAuthorizationCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Missing
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { AuthorizationShouldThrow = true };
        var service = CreateService(context);
        await service.RunDmarcAuthorizationCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(DmarcAuthorizationCheckStatus.Missing, verifyDomain.DmarcAuthorizationCheckStatus);
        Assert.Null(verifyDomain.DmarcAuthorizationCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcAuthorizationCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.DmarcAuthorizationCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checker = new FakeDmarcDnsChecker();
        var service = CreateService(context);
        await service.RunDmarcAuthorizationCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Empty(checker.AuthorizationCheckedDomains);

        await lockTransaction.RollbackAsync();
    }
}
