using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.DnsPush;

[Collection("Postgres")]
public sealed class DnsPushBatchWorkerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuditActor Person = AuditActor.ForUser("00000000-0000-0000-0000-0000000000a1", "person@example.com", "Person");

    private readonly PostgresContainerFixture _fixture;
    private readonly FakeDnsPushProvider _provider = new("cloudflare");
    private readonly ScriptedDnsChangeBuilder _dmarc = new("dmarc");
    private readonly ScriptedDnsProviderDetector _detector = new();
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly DnsPushBatchQueue _queue = new();
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsPushBatchWorkerTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DnsPushBatchWorker CreateWorker()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<DotMarcDbContext>>(new FakeDbContextFactory(_connectionString));
        services.AddSingleton<IDnsProviderDetector>(_detector);
        services.AddTransient<DnsPushPlanner>();
        services.AddSingleton<IEnumerable<IDnsChangeBuilder>>([_dmarc]);
        services.AddSingleton(new AuditRecorder(new FakeDbContextFactory(_connectionString), NullLogger<AuditRecorder>.Instance));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddTransient(provider => new DnsPushBatchRunner(
            provider.GetRequiredService<IDbContextFactory<DotMarcDbContext>>(), provider.GetRequiredService<DnsPushPlanner>(), provider.GetRequiredService<IEnumerable<IDnsChangeBuilder>>(),
            provider.GetRequiredService<AuditRecorder>(), provider.GetRequiredService<TimeProvider>(), (_, _) => Task.CompletedTask));
        var serviceProvider = services.BuildServiceProvider();
        return new DnsPushBatchWorker(_queue, serviceProvider.GetRequiredService<IServiceScopeFactory>(), [_provider], new FakeDbContextFactory(_connectionString), NullLogger<DnsPushBatchWorker>.Instance, _clock);
    }

    private async Task<Guid> RunningBatchAsync(params string[] domainNames)
    {
        await using var context = CreateContext();
        var domains = domainNames.Select(name => new Domain { Name = name, FirstSeenUtc = Now }).ToList();
        context.Domains.AddRange(domains);
        await context.SaveChangesAsync();
        var batch = await new DnsPushBatchService(new DnsPushPlanner(_detector), [_dmarc], [_provider], _clock)
            .CreateAsync(context, Person, "cloudflare", "dmarc", domains.Select(domain => domain.Id).ToList(), CancellationToken.None);
        Assert.True(await DnsPushBatchGate.ClaimAsync(context, batch.Id, Now, CancellationToken.None));
        return batch.Id;
    }

    private async Task<Guid> AddBatchAsync(DnsPushBatchStatus status, DateTimeOffset createdUtc, DateTimeOffset expiresUtc, DateTimeOffset? completedUtc = null)
    {
        await using var context = CreateContext();
        var batch = new DnsPushBatch
        {
            Id = Guid.NewGuid(), StartedBy = "key", Provider = "cloudflare", Target = "dmarc", Status = status, CreatedUtc = createdUtc, ExpiresUtc = expiresUtc, CompletedUtc = completedUtc,
            Domains = [new DnsPushBatchDomain { DomainId = 1, DomainName = "a.example", Position = 0, Included = true, Status = DnsPushBatchDomainStatus.Planned, PlanFingerprint = "f" }]
        };
        context.DnsPushBatches.Add(batch);
        await context.SaveChangesAsync();
        return batch.Id;
    }

    private async Task<DnsPushBatchStatus?> StatusOfAsync(Guid batchId)
    {
        await using var context = CreateContext();
        return (await context.DnsPushBatches.AsNoTracking().SingleOrDefaultAsync(batch => batch.Id == batchId))?.Status;
    }

    // ---- At startup ----

    [Fact]
    public async Task AtStartup_ABatchLeftRunning_IsInterrupted_AndItsUnfinishedDomainsNotAttempted()
    {
        var batchId = await AddBatchAsync(DnsPushBatchStatus.Running, Now.AddMinutes(-10), Now.AddMinutes(20));
        var planned = await AddBatchAsync(DnsPushBatchStatus.Planned, Now, Now.AddMinutes(30));

        await CreateWorker().RecoverAsync(CancellationToken.None);

        await using var context = CreateContext();
        var stored = await context.DnsPushBatches.AsNoTracking().Include(batch => batch.Domains).SingleAsync(batch => batch.Id == batchId);
        Assert.Equal(DnsPushBatchStatus.Interrupted, stored.Status);
        Assert.Equal(DnsPushBatchDomainStatus.NotAttempted, Assert.Single(stored.Domains).Status);
        Assert.Equal(DnsPushBatchStatus.Planned, await StatusOfAsync(planned));
    }

    // ---- Housekeeping ----

    [Fact]
    public async Task CleaningUp_ExpiresAPlannedBatchPastItsWindow_AndLeavesAnOpenOne()
    {
        var past = await AddBatchAsync(DnsPushBatchStatus.Planned, Now.AddMinutes(-40), Now.AddMinutes(-10));
        var open = await AddBatchAsync(DnsPushBatchStatus.Planned, Now, Now.AddMinutes(30));

        await CreateWorker().CleanUpAsync(CancellationToken.None);

        Assert.Equal((DnsPushBatchStatus.Expired, DnsPushBatchStatus.Planned), (await StatusOfAsync(past), await StatusOfAsync(open)));
    }

    [Fact]
    public async Task CleaningUp_DeletesFinishedBatchesOlderThanTheRetention_WithTheirRows_AndKeepsRecentOnes()
    {
        var old = await AddBatchAsync(DnsPushBatchStatus.Completed, Now.AddDays(-9), Now.AddDays(-9).AddMinutes(30), completedUtc: Now.AddDays(-8));
        var oldInterrupted = await AddBatchAsync(DnsPushBatchStatus.Interrupted, Now.AddDays(-9), Now.AddDays(-9).AddMinutes(30), completedUtc: Now.AddDays(-8));
        var oldExpired = await AddBatchAsync(DnsPushBatchStatus.Expired, Now.AddDays(-9), Now.AddDays(-8));
        var recent = await AddBatchAsync(DnsPushBatchStatus.Completed, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(30), completedUtc: Now.AddDays(-2));
        var running = await AddBatchAsync(DnsPushBatchStatus.Running, Now.AddDays(-9), Now.AddDays(-9).AddMinutes(30));

        await CreateWorker().CleanUpAsync(CancellationToken.None);

        Assert.Equal([null, null, null], new[] { await StatusOfAsync(old), await StatusOfAsync(oldInterrupted), await StatusOfAsync(oldExpired) });
        Assert.Equal(DnsPushBatchStatus.Completed, await StatusOfAsync(recent));
        Assert.Equal(DnsPushBatchStatus.Running, await StatusOfAsync(running));
        await using var context = CreateContext();
        Assert.Equal(2, await context.DnsPushBatchDomains.CountAsync());
    }

    // ---- Running work ----

    [Fact]
    public async Task ASignedInBatch_IsRunToCompletion_UsingTheSessionItWasGiven()
    {
        var batchId = await RunningBatchAsync("one.example", "two.example");

        await CreateWorker().ProcessAsync(new DnsPushBatchWork(batchId, "cloudflare", new FakeDnsPushProvider.Session(), Person), CancellationToken.None);

        Assert.Equal(DnsPushBatchStatus.Completed, await StatusOfAsync(batchId));
        Assert.Equal(["one.example", "two.example"], _provider.Pushes.Select(push => push.Zone));
    }

    [Fact]
    public async Task ABatchForAProviderThatIsntRegistered_IsInterrupted_NotLeftRunning()
    {
        var batchId = await RunningBatchAsync("one.example");

        await CreateWorker().ProcessAsync(new DnsPushBatchWork(batchId, "route53", new FakeDnsPushProvider.Session(), Person), CancellationToken.None);

        Assert.Equal(DnsPushBatchStatus.Interrupted, await StatusOfAsync(batchId));
        Assert.Empty(_provider.Pushes);
    }

    [Fact]
    public async Task TheRunningWorker_TakesBatchesOffTheQueueOneAtATime_InOrder()
    {
        var worker = CreateWorker();
        Guid first;
        Guid second;

        // Started first: at startup a batch already Running is Interrupted, because its session is gone. These are claimed while it runs.
        await worker.StartAsync(CancellationToken.None);
        try
        {
            first = await RunningBatchAsync("first-a.example", "first-b.example");
            second = await RunningBatchAsync("second-a.example");
            _queue.Enqueue(new DnsPushBatchWork(first, "cloudflare", new FakeDnsPushProvider.Session(), Person));
            _queue.Enqueue(new DnsPushBatchWork(second, "cloudflare", new FakeDnsPushProvider.Session(), Person));

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && (await StatusOfAsync(second)) != DnsPushBatchStatus.Completed)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal((DnsPushBatchStatus.Completed, DnsPushBatchStatus.Completed), (await StatusOfAsync(first), await StatusOfAsync(second)));
        Assert.Equal(["first-a.example", "first-b.example", "second-a.example"], _provider.Pushes.Select(push => push.Zone));
    }
}
