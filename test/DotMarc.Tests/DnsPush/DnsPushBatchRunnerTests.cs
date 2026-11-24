using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.DnsPush;

[Collection("Postgres")]
public sealed class DnsPushBatchRunnerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuditActor Person = AuditActor.ForUser("00000000-0000-0000-0000-0000000000a1", "person@example.com", "Person");

    private readonly PostgresContainerFixture _fixture;
    private readonly ScriptedDnsProviderDetector _detector = new();
    private readonly ScriptedDnsChangeBuilder _dmarc = new("dmarc");
    private readonly FakeDnsPushProvider _provider = new("cloudflare");
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly List<TimeSpan> _pauses = [];
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsPushBatchRunnerTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DnsPushBatchRunner CreateRunner() => new(
        new FakeDbContextFactory(_connectionString), new DnsPushPlanner(_detector), [_dmarc],
        new AuditRecorder(new FakeDbContextFactory(_connectionString), NullLogger<AuditRecorder>.Instance), _clock,
        (pause, _) => { _pauses.Add(pause); return Task.CompletedTask; });

    private async Task<(Guid BatchId, List<int> DomainIds)> ReviewedBatchAsync(params string[] domainNames)
    {
        await using var context = CreateContext();
        var domains = domainNames.Select(name => new Domain { Name = name, FirstSeenUtc = Now }).ToList();
        context.Domains.AddRange(domains);
        await context.SaveChangesAsync();
        var ids = domains.Select(domain => domain.Id).ToList();

        var service = new DnsPushBatchService(new DnsPushPlanner(_detector), [_dmarc], [_provider], _clock);
        var batch = await service.CreateAsync(context, Person, "cloudflare", "dmarc", ids, CancellationToken.None);
        return (batch.Id, ids);
    }

    private async Task RunAsync(Guid batchId, TimeSpan? pause = null, CancellationToken cancellationToken = default, bool claim = true)
    {
        if (claim)
        {
            await using var context = CreateContext();
            Assert.True(await DnsPushBatchGate.ClaimAsync(context, batchId, Now, CancellationToken.None));
        }

        await CreateRunner().RunAsync(batchId, new FakeDnsPushProvider.Session(), _provider, Person, pause ?? TimeSpan.Zero, cancellationToken);
    }

    private async Task<DnsPushBatch> ReloadAsync(Guid batchId)
    {
        await using var context = CreateContext();
        return await context.DnsPushBatches.AsNoTracking().Include(batch => batch.Domains).SingleAsync(batch => batch.Id == batchId);
    }

    private async Task<List<string>> AuditActionsAsync()
    {
        await using var context = CreateContext();
        return await context.AuditEntries.AsNoTracking().OrderBy(entry => entry.Id).Select(entry => entry.Action).ToListAsync();
    }

    private static List<DnsPushBatchDomainStatus> StatusesOf(DnsPushBatch batch) =>
        batch.Domains.OrderBy(row => row.Position).Select(row => row.Status).ToList();

    [Fact]
    public async Task AReviewedBatch_PushesEveryIncludedDomain_OnePushEach_AndCompletes()
    {
        var (batchId, _) = await ReviewedBatchAsync("one.example", "two.example", "three.example");

        await RunAsync(batchId);

        var batch = await ReloadAsync(batchId);
        Assert.Equal(DnsPushBatchStatus.Completed, batch.Status);
        Assert.Equal(Now, batch.CompletedUtc);
        Assert.All(batch.Domains, row => Assert.Equal((DnsPushBatchDomainStatus.Pushed, (DateTimeOffset?)Now), (row.Status, row.FinishedUtc)));
        Assert.Equal(["one.example", "two.example", "three.example"], _provider.Pushes.Select(push => push.Zone));
        Assert.Equal(["dns.pushed", "dns.pushed", "dns.pushed"], await AuditActionsAsync());
    }

    [Fact]
    public async Task ADomainWhosePlanChangedSinceReview_IsSkippedAndReported_NeverPushed()
    {
        var (batchId, _) = await ReviewedBatchAsync("one.example", "two.example");
        _dmarc.DesiredValues["two.example"] = "v=DMARC1; p=reject";

        await RunAsync(batchId);

        var batch = await ReloadAsync(batchId);
        Assert.Equal([DnsPushBatchDomainStatus.Pushed, DnsPushBatchDomainStatus.ChangedSinceReview], StatusesOf(batch));
        Assert.Equal(["one.example"], _provider.Pushes.Select(push => push.Zone));
        Assert.Equal(["dns.pushed"], await AuditActionsAsync());
        Assert.Equal(DnsPushBatchStatus.Completed, batch.Status);
    }

    [Fact]
    public async Task ADomainThatNowNeedsNothing_IsAlsoChangedSinceReview()
    {
        var (batchId, _) = await ReviewedBatchAsync("one.example");
        _dmarc.NothingToChange.Add("one.example");

        await RunAsync(batchId);

        Assert.Equal([DnsPushBatchDomainStatus.ChangedSinceReview], StatusesOf(await ReloadAsync(batchId)));
        Assert.Empty(_provider.Pushes);
    }

    [Fact]
    public async Task OneDomainFailing_DoesNotStopTheOthers_AndEachAttemptIsAudited()
    {
        var (batchId, _) = await ReviewedBatchAsync("fails.example", "half-done.example", "fine.example", "missing-zone.example");
        _provider.ResultsByZone["fails.example"] = new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare rejected the record push (500).");
        _provider.ResultsByZone["half-done.example"] = new DnsPushResult(DnsPushOutcome.ReplaceFailedAfterDelete, "create failed");
        _provider.ResultsByZone["missing-zone.example"] = new DnsPushResult(DnsPushOutcome.ZoneNotFound, "Couldn't find missing-zone.example in the Cloudflare account you authorized.");

        await RunAsync(batchId);

        var batch = await ReloadAsync(batchId);
        Assert.Equal(
            [DnsPushBatchDomainStatus.Failed, DnsPushBatchDomainStatus.FailedAfterChange, DnsPushBatchDomainStatus.Pushed, DnsPushBatchDomainStatus.ZoneNotFound],
            StatusesOf(batch));
        Assert.Equal(4, _provider.Pushes.Count);
        Assert.Contains("rejected the record push", batch.Domains.Single(row => row.DomainName == "fails.example").Detail);
        // A zone that was never found changed nothing, so it has no audit entry; the other three attempts each have one.
        Assert.Equal(["dns.push_failed", "dns.push_failed", "dns.pushed"], await AuditActionsAsync());
    }

    [Fact]
    public async Task ALeftoverDmarcRecord_FailsThatDomainAfterTheChange_IsAudited_AndTheBatchCarriesOn()
    {
        var (batchId, _) = await ReviewedBatchAsync("leftover.example", "fine.example");
        _provider.ResultsByZone["leftover.example"] = new DnsPushResult(DnsPushOutcome.LeftDuplicateDmarcRecord, "the name now has more than one DMARC record");

        await RunAsync(batchId);

        Assert.Equal(
            [DnsPushBatchDomainStatus.FailedAfterChange, DnsPushBatchDomainStatus.Pushed],
            StatusesOf(await ReloadAsync(batchId)));
        Assert.Equal(2, _provider.Pushes.Count);
        Assert.Equal(["dns.push_failed", "dns.pushed"], await AuditActionsAsync());
    }

    [Fact]
    public async Task ADomainWhoseDnsMovedToAnotherProviderSinceReview_IsZoneNotFound_AndNeverPushed()
    {
        var (batchId, _) = await ReviewedBatchAsync("moved.example", "stays.example");
        _detector.MovedTo("moved.example", DetectedDnsProvider.AzureDns);

        await RunAsync(batchId);

        Assert.Equal([DnsPushBatchDomainStatus.ZoneNotFound, DnsPushBatchDomainStatus.Pushed], StatusesOf(await ReloadAsync(batchId)));
        Assert.Equal(["stays.example"], _provider.Pushes.Select(push => push.Zone));
    }

    [Fact]
    public async Task AFailedLookupAtPushTime_IsAFailure_NotAPush()
    {
        var (batchId, _) = await ReviewedBatchAsync("flaky.example");
        _detector.Failing.Add("flaky.example");

        await RunAsync(batchId);

        var row = Assert.Single((await ReloadAsync(batchId)).Domains);
        Assert.Equal((DnsPushBatchDomainStatus.Failed, "error"), (row.Status, row.Detail));
        Assert.Empty(_provider.Pushes);
    }

    [Fact]
    public async Task ADomainThatWasUntickedInTheReview_IsNeverTouched()
    {
        var (batchId, ids) = await ReviewedBatchAsync("one.example", "two.example");
        await using (var context = CreateContext())
        {
            await new DnsPushBatchService(new DnsPushPlanner(_detector), [_dmarc], [_provider], _clock).SetIncludedAsync(context, Person, batchId, [ids[0]], CancellationToken.None);
        }

        await RunAsync(batchId);

        Assert.Equal([DnsPushBatchDomainStatus.Pushed, DnsPushBatchDomainStatus.Planned], StatusesOf(await ReloadAsync(batchId)));
        Assert.Equal(["one.example"], _provider.Pushes.Select(push => push.Zone));
    }

    [Fact]
    public async Task ADomainDeletedAfterReview_IsAFailure_AndTheRestStillRun()
    {
        var (batchId, ids) = await ReviewedBatchAsync("gone.example", "here.example");
        await using (var context = CreateContext())
        {
            await context.Domains.Where(domain => domain.Id == ids[0]).ExecuteDeleteAsync();
        }

        await RunAsync(batchId);

        var batch = await ReloadAsync(batchId);
        Assert.Equal([DnsPushBatchDomainStatus.Failed, DnsPushBatchDomainStatus.Pushed], StatusesOf(batch));
        Assert.Equal("not-found", batch.Domains.OrderBy(row => row.Position).First().Detail);
    }

    [Fact]
    public async Task ThePauseBetweenDomains_IsHonoured_ButNotAfterTheLastOne()
    {
        var (batchId, _) = await ReviewedBatchAsync("one.example", "two.example", "three.example");

        await RunAsync(batchId, pause: TimeSpan.FromMilliseconds(250));

        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)], _pauses);
    }

    [Fact]
    public async Task Cancelling_DuringAPush_MarksThatDomainFailed_BecauseTheZoneMayHaveChanged_AndTheRestNotAttempted()
    {
        var (batchId, _) = await ReviewedBatchAsync("one.example", "two.example", "three.example");
        using var cancellation = new CancellationTokenSource();
        _provider.BeforePush = zone =>
        {
            if (zone == "two.example")
            {
                cancellation.Cancel();
            }
        };

        await RunAsync(batchId, cancellationToken: cancellation.Token);

        var batch = await ReloadAsync(batchId);
        Assert.Equal(DnsPushBatchStatus.Interrupted, batch.Status);
        Assert.Equal([DnsPushBatchDomainStatus.Pushed, DnsPushBatchDomainStatus.Failed, DnsPushBatchDomainStatus.NotAttempted], StatusesOf(batch));
        Assert.Contains("check this domain's DNS", batch.Domains.Single(row => row.DomainName == "two.example").Detail);
    }

    [Fact]
    public async Task CancellingBetweenDomains_MarksEveryDomainNotYetStartedNotAttempted()
    {
        var (batchId, _) = await ReviewedBatchAsync("one.example", "two.example");
        using var cancellation = new CancellationTokenSource();
        await using (var context = CreateContext())
        {
            Assert.True(await DnsPushBatchGate.ClaimAsync(context, batchId, Now, CancellationToken.None));
        }

        await cancellation.CancelAsync();
        await CreateRunner().RunAsync(batchId, new FakeDnsPushProvider.Session(), _provider, Person, TimeSpan.Zero, cancellation.Token);

        var batch = await ReloadAsync(batchId);
        Assert.Equal(DnsPushBatchStatus.Interrupted, batch.Status);
        Assert.Equal([DnsPushBatchDomainStatus.NotAttempted, DnsPushBatchDomainStatus.NotAttempted], StatusesOf(batch));
        Assert.Empty(_provider.Pushes);
    }

    [Fact]
    public async Task AProviderThatThrows_IsAFailureForThatDomain_WithNoMessageKept_AndTheRestStillRun()
    {
        var (batchId, _) = await ReviewedBatchAsync("throws.example", "fine.example");
        _provider.BeforePush = zone =>
        {
            if (zone == "throws.example")
            {
                throw new InvalidOperationException("secret-looking-token-abc123");
            }
        };

        await RunAsync(batchId);

        var batch = await ReloadAsync(batchId);
        Assert.Equal([DnsPushBatchDomainStatus.Failed, DnsPushBatchDomainStatus.Pushed], StatusesOf(batch));
        Assert.DoesNotContain("secret-looking-token", batch.Domains.OrderBy(row => row.Position).First().Detail);
    }

    [Fact]
    public async Task ABatchThatIsNotRunning_IsLeftAlone()
    {
        var (batchId, _) = await ReviewedBatchAsync("one.example");

        await RunAsync(batchId, claim: false);

        Assert.Empty(_provider.Pushes);
        Assert.Equal(DnsPushBatchStatus.Planned, (await ReloadAsync(batchId)).Status);
    }

    [Fact]
    public async Task AProvidersMessage_IsCutToFitTheColumn()
    {
        var (batchId, _) = await ReviewedBatchAsync("one.example");
        _provider.ResultsByZone["one.example"] = new DnsPushResult(DnsPushOutcome.ProviderError, new string('x', 2000));

        await RunAsync(batchId);

        var detail = Assert.Single((await ReloadAsync(batchId)).Domains).Detail!;
        Assert.True(detail.Length <= 500, "A provider's message is cut to fit the column.");
    }
}
