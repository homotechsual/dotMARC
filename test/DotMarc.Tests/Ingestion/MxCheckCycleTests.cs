using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class MxCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public MxCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task RunMxCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeMxDnsChecker { Result = new(MxCheckStatus.MissingRecord, "No MX record") };
        var service = CreateService(context);
        await service.RunMxCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Contains("contoso.io", checker.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(MxCheckStatus.MissingRecord, domain.MxCheckStatus);
        Assert.NotNull(domain.MxCheckedUtc);
    }

    [Fact]
    public async Task RunMxCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MxCheckStatus = MxCheckStatus.Ok,
            MxCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var checker = new FakeMxDnsChecker();
        var service = CreateService(context);
        await service.RunMxCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().MxCheckedUtc);
    }

    [Fact]
    public async Task RunMxCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MxCheckStatus = MxCheckStatus.MissingRecord
        });
        await context.SaveChangesAsync();

        var checker = new FakeMxDnsChecker { ShouldThrow = true };
        var service = CreateService(context);
        await service.RunMxCheckCycleAsync(context, checker, CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(MxCheckStatus.MissingRecord, verifyDomain.MxCheckStatus);
        Assert.Null(verifyDomain.MxCheckedUtc);
    }

    [Fact]
    public async Task RunMxCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.MxCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checker = new FakeMxDnsChecker();
        var service = CreateService(context);
        await service.RunMxCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);

        await lockTransaction.RollbackAsync();
    }
}
