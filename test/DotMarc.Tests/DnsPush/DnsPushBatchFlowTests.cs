using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.DnsPush;

/// <summary>The whole of a batch with fakes for the provider: reviewed, a subset kept, signed in to, and pushed.</summary>
[Collection("Postgres")]
public sealed class DnsPushBatchFlowTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuditActor Person = AuditActor.ForUser("00000000-0000-0000-0000-0000000000a1", "person@example.com", "Person");

    private readonly PostgresContainerFixture _fixture;
    private readonly FakeDnsPushProvider _provider = new("cloudflare");
    private readonly ScriptedDnsChangeBuilder _dmarc = new("dmarc");
    private readonly ScriptedDnsProviderDetector _detector = new();
    private readonly FixedTimeProvider _clock = new(Now);
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsPushBatchFlowTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private sealed class AllowEverything : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) => Task.FromResult(AuthorizationResult.Success());
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task CreateIncludeSubsetSignInRun_LeavesTheRightRows_AndOneAuditEntryPerAttemptPlusTheBatchOne()
    {
        await using var context = CreateContext();
        var domains = new[] { "one.example", "two.example", "three.example", "four.example" }.Select(name => new Domain { Name = name, FirstSeenUtc = Now }).ToList();
        context.Domains.AddRange(domains);
        await context.SaveChangesAsync();
        var ids = domains.Select(domain => domain.Id).ToList();

        var service = new DnsPushBatchService(new DnsPushPlanner(_detector), [_dmarc], [_provider], _clock);
        var batch = await service.CreateAsync(context, Person, "cloudflare", "dmarc", ids, CancellationToken.None);
        await service.SetIncludedAsync(context, Person, batch.Id, [ids[0], ids[1], ids[3]], CancellationToken.None);
        _dmarc.DesiredValues["four.example"] = "v=DMARC1; p=reject";
        _provider.ResultsByZone["two.example"] = new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare rejected the record push (500).");

        var queue = new DnsPushBatchQueue();
        var callback = new DnsPushBatchCallback([_dmarc], new AllowEverything(), queue, _clock);
        var state = new DnsPushState(0, "dmarc", "verifier", Now.AddMinutes(5), null, batch.Id.ToString(), batch.Id);
        Assert.Equal("batch-started", await callback.HandleAsync(context, _provider, state, "code", null, "https://dotmarc.example/cb", new ClaimsPrincipal(), Person, CancellationToken.None));

        Assert.True(queue.Reader.TryRead(out var work));
        var runner = new DnsPushBatchRunner(new FakeDbContextFactory(_connectionString), new DnsPushPlanner(_detector), [_dmarc],
            new AuditRecorder(new FakeDbContextFactory(_connectionString), NullLogger<AuditRecorder>.Instance), _clock, (_, _) => Task.CompletedTask);
        await runner.RunAsync(work!.BatchId, work.Session, _provider, work.Actor, TimeSpan.Zero, CancellationToken.None);

        await using var check = CreateContext();
        var stored = await check.DnsPushBatches.AsNoTracking().Include(candidate => candidate.Domains).SingleAsync();
        Assert.Equal(DnsPushBatchStatus.Completed, stored.Status);
        Assert.Equal(
            [DnsPushBatchDomainStatus.Pushed, DnsPushBatchDomainStatus.Failed, DnsPushBatchDomainStatus.Planned, DnsPushBatchDomainStatus.ChangedSinceReview],
            stored.Domains.OrderBy(row => row.Position).Select(row => row.Status));
        Assert.Equal(["one.example"], _provider.Pushes.Where(push => push.Zone != "two.example").Select(push => push.Zone));
        Assert.Equal(1, _provider.ExchangeCalls);

        // The batch started, then one entry for each domain a push was attempted for (one.example pushed, two.example failed). The skipped
        // and the unticked domains have none.
        Assert.Equal(
            [AuditActions.DnsPushBatchStarted, AuditActions.DnsPushed, AuditActions.DnsPushFailed],
            await check.AuditEntries.AsNoTracking().OrderBy(entry => entry.Id).Select(entry => entry.Action).ToListAsync());
    }
}
