using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class DkimCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DkimCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task RunDkimCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, DkimSelectors = ["selector1"] });
        await context.SaveChangesAsync();

        var checker = new FakeDkimDnsChecker { Result = new(DkimCheckStatus.Missing, "No DKIM record") };
        var service = CreateService(context);
        await service.RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Contains("contoso.io", checker.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(DkimCheckStatus.Missing, domain.DkimCheckStatus);
        Assert.NotNull(domain.DkimCheckedUtc);
    }

    [Fact]
    public async Task RunDkimCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DkimSelectors = ["selector1"],
            DkimCheckStatus = DkimCheckStatus.Ok,
            DkimCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var checker = new FakeDkimDnsChecker();
        var service = CreateService(context);
        await service.RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().DkimCheckedUtc);
    }

    [Fact]
    public async Task RunDkimCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DkimSelectors = ["selector1"],
            DkimCheckStatus = DkimCheckStatus.Missing
        });
        await context.SaveChangesAsync();

        var checker = new FakeDkimDnsChecker { ShouldThrow = true };
        var service = CreateService(context);
        await service.RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(DkimCheckStatus.Missing, verifyDomain.DkimCheckStatus);
        Assert.Null(verifyDomain.DkimCheckedUtc);
    }

    [Fact]
    public async Task RunDkimCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, DkimSelectors = ["selector1"] });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.DkimCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checker = new FakeDkimDnsChecker();
        var service = CreateService(context);
        await service.RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);

        await lockTransaction.RollbackAsync();
    }

    [Fact]
    public async Task RunDkimCheckCycleAsync_SetsNotConfigured_WhenNoSelectorsAreConfigured()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeDkimDnsChecker { Result = new(DkimCheckStatus.Ok, null) };
        var service = CreateService(context);
        await service.RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains); // short-circuited before ever calling the checker
        var domain = context.Domains.Single();
        Assert.Equal(DkimCheckStatus.NotConfigured, domain.DkimCheckStatus);
        Assert.NotNull(domain.DkimCheckedUtc);
    }
}
