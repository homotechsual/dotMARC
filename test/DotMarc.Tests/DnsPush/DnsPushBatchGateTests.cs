using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.DnsPush;

[Collection("Postgres")]
public sealed class DnsPushBatchGateTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Owner = "owner-key";

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsPushBatchGateTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DotMarc.Data.DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarc.Data.DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private async Task<Guid> AddBatchAsync(
        DnsPushBatchStatus status = DnsPushBatchStatus.Planned, string provider = "cloudflare", DateTimeOffset? expiresUtc = null, bool includeADomain = true)
    {
        await using var context = CreateContext();
        var batch = new DnsPushBatch
        {
            Id = Guid.NewGuid(), StartedBy = Owner, Provider = provider, Target = "dmarc", Status = status, CreatedUtc = Now, ExpiresUtc = expiresUtc ?? Now.AddMinutes(30),
            Domains =
            [
                new DnsPushBatchDomain { DomainId = 1, DomainName = "a.example", Position = 0, Included = includeADomain, Status = DnsPushBatchDomainStatus.Planned, PlanFingerprint = "f" },
                new DnsPushBatchDomain { DomainId = 2, DomainName = "b.example", Position = 1, Included = false, Status = DnsPushBatchDomainStatus.Planned, PlanFingerprint = "g" },
                new DnsPushBatchDomain { DomainId = 3, DomainName = "c.example", Position = 2, Included = false, Status = DnsPushBatchDomainStatus.Refused, Detail = "zone-not-found" }
            ]
        };
        context.DnsPushBatches.Add(batch);
        await context.SaveChangesAsync();
        return batch.Id;
    }

    private async Task<DnsPushBatchCheck> CheckAsync(Guid batchId, string? person = Owner, string provider = "cloudflare", DateTimeOffset? now = null)
    {
        await using var context = CreateContext();
        return await DnsPushBatchGate.CheckAsync(context, batchId, person, provider, now ?? Now, CancellationToken.None);
    }

    // ---- Checking a batch before anyone signs in ----

    [Fact]
    public async Task APlannedBatch_OfThePersonWhoStartedIt_ForItsProvider_PassesTheCheck()
    {
        var check = await CheckAsync(await AddBatchAsync());

        Assert.Null(check.Problem);
        Assert.Equal("dmarc", check.Batch!.Target);
    }

    [Fact]
    public async Task EachWayABatchCanBeUnusable_HasItsOwnProblem()
    {
        var missing = await CheckAsync(Guid.NewGuid());
        var someoneElses = await CheckAsync(await AddBatchAsync(), person: "someone-else");
        var noPerson = await CheckAsync(await AddBatchAsync(), person: null);
        var wrongProvider = await CheckAsync(await AddBatchAsync(), provider: "azure-dns");
        var running = await CheckAsync(await AddBatchAsync(DnsPushBatchStatus.Running));
        var completed = await CheckAsync(await AddBatchAsync(DnsPushBatchStatus.Completed));
        var expired = await CheckAsync(await AddBatchAsync(), now: Now.AddMinutes(31));
        var nothingIncluded = await CheckAsync(await AddBatchAsync(includeADomain: false));

        Assert.Equal(
            [DnsPushBatchProblem.NotFound, DnsPushBatchProblem.SomeoneElses, DnsPushBatchProblem.SomeoneElses, DnsPushBatchProblem.WrongProvider,
                DnsPushBatchProblem.NotPlanned, DnsPushBatchProblem.NotPlanned, DnsPushBatchProblem.Expired, DnsPushBatchProblem.NothingIncluded],
            new[] { missing, someoneElses, noPerson, wrongProvider, running, completed, expired, nothingIncluded }.Select(check => check.Problem!.Value));
        Assert.All(new[] { missing, someoneElses, noPerson, wrongProvider, running, completed, expired, nothingIncluded }, check => Assert.Null(check.Batch));
    }

    [Fact]
    public async Task TheProviderComparisonIgnoresCase()
    {
        Assert.Null((await CheckAsync(await AddBatchAsync(), provider: "Cloudflare")).Problem);
    }

    // ---- Claiming a batch ----

    [Fact]
    public async Task ABatch_CanBeClaimedOnce_AndIsThenRunningWithAStartTime()
    {
        var batchId = await AddBatchAsync();
        await using var first = CreateContext();
        await using var second = CreateContext();

        var firstClaim = await DnsPushBatchGate.ClaimAsync(first, batchId, Now, CancellationToken.None);
        var secondClaim = await DnsPushBatchGate.ClaimAsync(second, batchId, Now, CancellationToken.None);

        Assert.True(firstClaim);
        Assert.False(secondClaim);
        await using var verify = CreateContext();
        var stored = await verify.DnsPushBatches.AsNoTracking().SingleAsync(batch => batch.Id == batchId);
        Assert.Equal((DnsPushBatchStatus.Running, (DateTimeOffset?)Now), (stored.Status, stored.StartedUtc));
    }

    [Fact]
    public async Task ASimultaneousClaim_IsWonByExactlyOne()
    {
        var batchId = await AddBatchAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = CreateContext();
            return await DnsPushBatchGate.ClaimAsync(context, batchId, Now, CancellationToken.None);
        }));

        Assert.Equal(1, claims.Count(won => won));
    }

    [Fact]
    public async Task AnExpiredBatch_CannotBeClaimed()
    {
        var batchId = await AddBatchAsync();
        await using var context = CreateContext();

        Assert.False(await DnsPushBatchGate.ClaimAsync(context, batchId, Now.AddMinutes(31), CancellationToken.None));
    }

    [Fact]
    public async Task AbandoningARunningBatch_MarksItInterrupted_AndItsUnfinishedIncludedDomainsNotAttempted()
    {
        var batchId = await AddBatchAsync();
        await using (var context = CreateContext())
        {
            await DnsPushBatchGate.ClaimAsync(context, batchId, Now, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            await DnsPushBatchGate.AbandonAsync(context, batchId, Now.AddMinutes(1), CancellationToken.None);
        }

        await using var verify = CreateContext();
        var stored = await verify.DnsPushBatches.AsNoTracking().Include(batch => batch.Domains).SingleAsync(batch => batch.Id == batchId);
        Assert.Equal(DnsPushBatchStatus.Interrupted, stored.Status);
        Assert.Equal(Now.AddMinutes(1), stored.CompletedUtc);
        var rows = stored.Domains.OrderBy(row => row.Position).ToList();
        Assert.Equal([DnsPushBatchDomainStatus.NotAttempted, DnsPushBatchDomainStatus.Planned, DnsPushBatchDomainStatus.Refused], rows.Select(row => row.Status));
    }
}
