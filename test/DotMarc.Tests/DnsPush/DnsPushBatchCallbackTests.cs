using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.DnsPush;

[Collection("Postgres")]
public sealed class DnsPushBatchCallbackTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuditActor Person = AuditActor.ForUser("00000000-0000-0000-0000-0000000000a1", "person@example.com", "Person");
    private static readonly AuditActor AnotherPerson = AuditActor.ForUser("00000000-0000-0000-0000-0000000000b2", "other@example.com", "Other");
    private const string RedirectUri = "https://dotmarc.example/dns-push/cloudflare/callback";

    private readonly PostgresContainerFixture _fixture;
    private readonly FakeDnsPushProvider _provider = new("cloudflare");
    private readonly DnsPushBatchQueue _queue = new();
    private readonly FixedTimeProvider _clock = new(Now);
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsPushBatchCallbackTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private sealed class FakeAuthorizationService(bool allow) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(allow ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(allow ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private DnsPushBatchCallback CreateCallback(bool allowed = true) =>
        new([new ScriptedDnsChangeBuilder("dmarc"), new ScriptedDnsChangeBuilder("tlsrpt")], new FakeAuthorizationService(allowed), _queue, _clock);

    private async Task<Guid> AddBatchAsync(AuditActor? owner = null, DateTimeOffset? expiresUtc = null)
    {
        await using var context = CreateContext();
        var batch = new DnsPushBatch
        {
            Id = Guid.NewGuid(), StartedBy = DotMarc.Reporting.DashboardPreferencesStore.UserKeyOf(owner ?? Person)!, Provider = "cloudflare", Target = "dmarc",
            Status = DnsPushBatchStatus.Planned, CreatedUtc = Now, ExpiresUtc = expiresUtc ?? Now.AddMinutes(30),
            Domains = [new DnsPushBatchDomain { DomainId = 1, DomainName = "a.example", Position = 0, Included = true, Status = DnsPushBatchDomainStatus.Planned, PlanFingerprint = "f" }]
        };
        context.DnsPushBatches.Add(batch);
        await context.SaveChangesAsync();
        return batch.Id;
    }

    private async Task<string> HandleAsync(Guid batchId, AuditActor? actor = null, string? code = "code", string? error = null, bool allowed = true, string target = "dmarc")
    {
        await using var context = CreateContext();
        var state = new DnsPushState(0, target, "verifier", Now.AddMinutes(5), null, batchId.ToString(), batchId);
        return await CreateCallback(allowed).HandleAsync(context, _provider, state, code, error, RedirectUri, new ClaimsPrincipal(), actor ?? Person, CancellationToken.None);
    }

    private async Task<DnsPushBatchStatus> StatusOfAsync(Guid batchId)
    {
        await using var context = CreateContext();
        return (await context.DnsPushBatches.AsNoTracking().SingleAsync(batch => batch.Id == batchId)).Status;
    }

    [Fact]
    public async Task AGoodBatch_IsClaimed_ExchangedOnce_AndHandedToTheWorkerWithItsSession()
    {
        var batchId = await AddBatchAsync();

        var flag = await HandleAsync(batchId);

        Assert.Equal("batch-started", flag);
        Assert.Equal(DnsPushBatchStatus.Running, await StatusOfAsync(batchId));
        Assert.Equal(1, _provider.ExchangeCalls);
        Assert.True(_queue.Reader.TryRead(out var work));
        Assert.Equal((batchId, "cloudflare", Person), (work!.BatchId, work.Provider, work.Actor));
        Assert.IsType<FakeDnsPushProvider.Session>(work.Session);
        Assert.False(_queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StartingABatch_RecordsOneAuditEntry_WithTheCounts_AndNothingSecret()
    {
        var batchId = await AddBatchAsync();

        await HandleAsync(batchId, code: "secret-code-123");

        await using var context = CreateContext();
        var entry = Assert.Single(await context.AuditEntries.AsNoTracking().ToListAsync());
        Assert.Equal((AuditActions.DnsPushBatchStarted, AuditEntryKind.Change, null), (entry.Action, entry.Kind, entry.TargetType));
        Assert.Equal("Started a DNS push of 1 domain to cloudflare (dmarc)", entry.Summary);
        Assert.Empty(entry.Changes);
        Assert.DoesNotContain("secret-code-123", entry.Summary);
        Assert.DoesNotContain("verifier", entry.Summary);
    }

    [Fact]
    public async Task ABatchThatIsRefused_RecordsNoAuditEntry()
    {
        var batchId = await AddBatchAsync();

        await HandleAsync(batchId, actor: AnotherPerson);
        await HandleAsync(batchId, code: null);

        await using var context = CreateContext();
        Assert.Empty(await context.AuditEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ASecondCallbackForTheSameBatch_IsInvalid_AndExchangesNothingMore()
    {
        var batchId = await AddBatchAsync();
        await HandleAsync(batchId);

        var second = await HandleAsync(batchId, code: "another-code");

        Assert.Equal("invalid", second);
        Assert.Equal(1, _provider.ExchangeCalls);
    }

    [Fact]
    public async Task SomeoneElsesBatch_IsInvalid_NothingIsExchanged_AndTheBatchStaysPlanned()
    {
        var batchId = await AddBatchAsync();

        var flag = await HandleAsync(batchId, actor: AnotherPerson);

        Assert.Equal("invalid", flag);
        Assert.Equal(0, _provider.ExchangeCalls);
        Assert.Equal(DnsPushBatchStatus.Planned, await StatusOfAsync(batchId));
    }

    [Fact]
    public async Task WithoutThePermissionToPushTheTarget_ItIsInvalid_AndNothingHappens()
    {
        var batchId = await AddBatchAsync();

        var flag = await HandleAsync(batchId, allowed: false);

        Assert.Equal("invalid", flag);
        Assert.Equal(0, _provider.ExchangeCalls);
        Assert.Equal(DnsPushBatchStatus.Planned, await StatusOfAsync(batchId));
    }

    [Fact]
    public async Task AStateForADifferentTargetThanTheBatch_IsInvalid()
    {
        var batchId = await AddBatchAsync();

        Assert.Equal("invalid", await HandleAsync(batchId, target: "tlsrpt"));
        Assert.Equal(DnsPushBatchStatus.Planned, await StatusOfAsync(batchId));
    }

    [Fact]
    public async Task AnExpiredBatch_IsInvalid()
    {
        var batchId = await AddBatchAsync(expiresUtc: Now.AddMinutes(-1));

        Assert.Equal("invalid", await HandleAsync(batchId));
        Assert.Equal(0, _provider.ExchangeCalls);
    }

    [Fact]
    public async Task CancellingAtTheProvider_IsCancelled_AndTheBatchStaysPlannedSoItCanBeTriedAgain()
    {
        var batchId = await AddBatchAsync();

        var cancelled = await HandleAsync(batchId, code: null, error: "access_denied");
        var noCode = await HandleAsync(batchId, code: null);

        Assert.Equal(["cancelled", "cancelled"], [cancelled, noCode]);
        Assert.Equal(0, _provider.ExchangeCalls);
        Assert.Equal(DnsPushBatchStatus.Planned, await StatusOfAsync(batchId));
    }

    [Fact]
    public async Task ARejectedAuthorizationCode_IsAnError_TheBatchIsInterrupted_AndNothingIsQueued()
    {
        var batchId = await AddBatchAsync();
        _provider.ExchangeFailure = new DnsPushResult(DnsPushOutcome.ProviderError, "rejected");

        var flag = await HandleAsync(batchId);

        Assert.Equal("error", flag);
        Assert.Equal(DnsPushBatchStatus.Interrupted, await StatusOfAsync(batchId));
        Assert.False(_queue.Reader.TryRead(out _));
        await using var context = CreateContext();
        Assert.Equal(DnsPushBatchDomainStatus.NotAttempted, (await context.DnsPushBatchDomains.AsNoTracking().SingleAsync()).Status);
    }
}
