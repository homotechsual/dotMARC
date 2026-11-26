using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using System.Security.Claims;
using System.Text.Json;
using DotMarc.Security;

namespace DotMarc.Tests.DnsPush;

[Collection("Postgres")]
public sealed class DnsPushBatchServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuditActor Person = AuditActor.ForUser("00000000-0000-0000-0000-0000000000a1", "person@example.com", "Person");
    private static readonly AuditActor AnotherPerson = AuditActor.ForUser("00000000-0000-0000-0000-0000000000b2", "other@example.com", "Other");

    private readonly PostgresContainerFixture _fixture;
    private readonly ScriptedDnsProviderDetector _detector = new();
    private readonly ScriptedDnsChangeBuilder _dmarc = new("dmarc");
    private readonly FixedTimeProvider _clock = new(Now);
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsPushBatchServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString)
            .ConfigureWarnings(warnings => warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning)).Options);

    private DnsPushBatchService CreateService()
    {
        IDnsChangeBuilder[] builders = [_dmarc, new ScriptedDnsChangeBuilder("tlsrpt"), new ScriptedDnsChangeBuilder("mta-sts"), new ScriptedDnsChangeBuilder("spf"), new ScriptedDnsChangeBuilder("dkim"), new ScriptedDnsChangeBuilder("dmarc-auth", writesToDomainZone: false)];
        return new DnsPushBatchService(new DnsPushPlanner(_detector), builders, [new FakeDnsPushProvider("cloudflare"), new FakeDnsPushProvider("azure-dns")], _clock);
    }

    private async Task<List<int>> AddDomainsAsync(params string[] names)
    {
        await using var context = CreateContext();
        var domains = names.Select(name => new Domain { Name = name, FirstSeenUtc = Now }).ToList();
        context.Domains.AddRange(domains);
        await context.SaveChangesAsync();
        return domains.Select(domain => domain.Id).ToList();
    }

    private async Task<DnsPushBatch> CreateBatchAsync(IReadOnlyList<int> domainIds, string provider = "cloudflare", string target = "dmarc", AuditActor? actor = null)
    {
        await using var context = CreateContext();
        return await CreateService().CreateAsync(context, actor ?? Person, provider, target, domainIds, CancellationToken.None);
    }

    private async Task<DnsPushBatch> ReloadAsync(Guid batchId)
    {
        await using var context = CreateContext();
        return await context.DnsPushBatches.AsNoTracking().Include(batch => batch.Domains).SingleAsync(batch => batch.Id == batchId);
    }

    [Fact]
    public async Task StoredReviewContainsExactValuesAndIsOnlyReadableByItsOwner()
    {
        var ids = await AddDomainsAsync("changes.example");
        var batch = await CreateBatchAsync(ids);
        await using var db = CreateContext();
        var read = await DnsPushBatchService.GetAsync(db, Person, batch.Id);
        var review = JsonSerializer.Deserialize<DnsPushReview>(Assert.Single(read.Domains).ReviewJson!)!;
        Assert.Equal("_dmarc.changes.example", Assert.Single(review.Additions).Name);
        var denied = await Assert.ThrowsAsync<DnsPushBatchException>(() => DnsPushBatchService.GetAsync(db, AnotherPerson, batch.Id));
        var missing = await Assert.ThrowsAsync<DnsPushBatchException>(() => DnsPushBatchService.GetAsync(db, Person, Guid.NewGuid()));
        Assert.Equal(missing.Message, denied.Message);
    }

    private sealed class DelegatedBuilder : IDnsChangeBuilder
    {
        public string Target => "dmarc";
        public string RequiredPolicy => "DomainsEdit";
        public bool WritesToDomainZone => true;
        public Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_dmarc.{request.Domain.Name}",
                "v=DMARC1; p=reject", "delegated.example", request.DomainZone!, ExistingRecordType: "CNAME")));
    }

    [Fact]
    public async Task DelegatedDomainsRequireExplicitIndividualInclusion()
    {
        var ids = await AddDomainsAsync("delegated.example");
        var service = new DnsPushBatchService(new DnsPushPlanner(_detector), [new DelegatedBuilder()], [new FakeDnsPushProvider("cloudflare")], _clock);
        await using var db = CreateContext();
        var batch = await service.CreateAsync(db, Person, "cloudflare", "dmarc", ids);
        var row = Assert.Single(batch.Domains);
        Assert.False(row.Included);
        Assert.Single(JsonSerializer.Deserialize<DnsPushReview>(row.ReviewJson!)!.LostDelegations);
        await service.SetIncludedAsync(db, Person, batch.Id, ids);
        Assert.True(Assert.Single((await ReloadAsync(batch.Id)).Domains).Included);
    }

    [Fact]
    public async Task ScopeChecksRequireEverySelectedDomainToRemainAccessible()
    {
        var ids = await AddDomainsAsync("one.example", "two.example");
        await using var db = CreateContext();
        var group = new Group { Name = "Allowed" };
        (await db.Domains.SingleAsync(domain => domain.Id == ids[0])).Groups.Add(group);
        await db.SaveChangesAsync();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))], "Test"));
        Assert.True(await DnsPushBatchAccess.InScopeAsync(db, user, [ids[0]]));
        Assert.False(await DnsPushBatchAccess.InScopeAsync(db, user, ids));
        Assert.False(await DnsPushBatchAccess.InScopeAsync(db, user, [int.MaxValue]));
    }

    // ---- Creating a batch ----

    [Fact]
    public async Task ABatch_ReviewsEachDomain_PlannedNothingToChangeOrRefused_InTheOrderGiven()
    {
        var ids = await AddDomainsAsync("changes.example", "already-correct.example", "refused.example");
        _dmarc.NothingToChange.Add("already-correct.example");
        _dmarc.Refusals["refused.example"] = "dmarc-cname-delegated";

        var batch = await CreateBatchAsync(ids);

        var stored = await ReloadAsync(batch.Id);
        Assert.Equal(("cloudflare", "dmarc", DnsPushBatchStatus.Planned), (stored.Provider, stored.Target, stored.Status));
        Assert.Equal(Now, stored.CreatedUtc);
        Assert.Equal(Now.AddMinutes(30), stored.ExpiresUtc);
        Assert.Null(stored.StartedUtc);
        var rows = stored.Domains.OrderBy(row => row.Position).ToList();
        Assert.Equal(["changes.example", "already-correct.example", "refused.example"], rows.Select(row => row.DomainName));
        Assert.Equal([DnsPushBatchDomainStatus.Planned, DnsPushBatchDomainStatus.NothingToChange, DnsPushBatchDomainStatus.Refused], rows.Select(row => row.Status));
        Assert.Equal([true, false, false], rows.Select(row => row.Included));
        Assert.Equal("dmarc-cname-delegated", rows[2].Detail);
    }

    [Fact]
    public async Task ThePlannedRowsFingerprint_IsTheFingerprintOfItsPlan()
    {
        var ids = await AddDomainsAsync("changes.example");
        _dmarc.DesiredValues["changes.example"] = "v=DMARC1; p=reject";

        var batch = await CreateBatchAsync(ids);

        var expected = DnsPlanFingerprint.Of([new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "_dmarc.changes.example", "v=DMARC1; p=reject", null, "changes.example")]);
        Assert.Equal(expected, Assert.Single((await ReloadAsync(batch.Id)).Domains).PlanFingerprint);
    }

    [Fact]
    public async Task ADomainWhoseDnsIsOnAnotherProvider_IsRefusedAsZoneNotFound()
    {
        var ids = await AddDomainsAsync("moved.example");
        _detector.MovedTo("moved.example", DetectedDnsProvider.AzureDns);

        var batch = await CreateBatchAsync(ids);

        var row = Assert.Single((await ReloadAsync(batch.Id)).Domains);
        Assert.Equal((DnsPushBatchDomainStatus.Refused, "zone-not-found", false), (row.Status, row.Detail, row.Included));
    }

    [Fact]
    public async Task AFailedDnsLookup_IsRefusedAsAnError()
    {
        var ids = await AddDomainsAsync("flaky.example");
        _detector.Failing.Add("flaky.example");

        var batch = await CreateBatchAsync(ids);

        Assert.Equal("error", Assert.Single((await ReloadAsync(batch.Id)).Domains).Detail);
    }

    [Fact]
    public async Task AnIdThatMatchesNoDomain_IsRefusedAsNotFound_AndTheRestAreStillPlanned()
    {
        var ids = await AddDomainsAsync("real.example");

        var batch = await CreateBatchAsync([ids[0], 987654]);

        var rows = (await ReloadAsync(batch.Id)).Domains.OrderBy(row => row.Position).ToList();
        Assert.Equal(DnsPushBatchDomainStatus.Planned, rows[0].Status);
        Assert.Equal((987654, DnsPushBatchDomainStatus.Refused, "not-found", ""), (rows[1].DomainId, rows[1].Status, rows[1].Detail, rows[1].DomainName));
    }

    [Fact]
    public async Task ADomainListedTwice_IsPlannedOnce()
    {
        var ids = await AddDomainsAsync("once.example");

        var batch = await CreateBatchAsync([ids[0], ids[0]]);

        Assert.Single((await ReloadAsync(batch.Id)).Domains);
    }

    [Fact]
    public async Task ABatchOfExactlyTheLimit_IsAllowed_AndOneMoreIsRefused()
    {
        var ids = await AddDomainsAsync(Enumerable.Range(0, DnsPushBatchLimits.MaxDomains + 1).Select(index => $"d{index}.example").ToArray());

        var allowed = await CreateBatchAsync(ids.Take(DnsPushBatchLimits.MaxDomains).ToList());
        var exception = await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateBatchAsync(ids));

        Assert.Equal(DnsPushBatchLimits.MaxDomains, (await ReloadAsync(allowed.Id)).Domains.Count);
        Assert.Contains("at most 50", exception.Message);
    }

    [Fact]
    public async Task AnEmptyList_ATargetWithPerDomainContent_AnUnknownProvider_AndAnApiKey_AreEachRefused_AndStoreNothing()
    {
        var ids = await AddDomainsAsync("a.example");

        await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateBatchAsync([]));
        foreach (var target in new[] { "spf", "dkim", "dmarc-auth", "nonsense" })
        {
            await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateBatchAsync(ids, target: target));
        }

        await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateBatchAsync(ids, provider: "route53"));
        await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateBatchAsync(ids, actor: AuditActor.ForApiKey(7, "an integration", "someone")));

        await using var context = CreateContext();
        Assert.Empty(await context.DnsPushBatches.ToListAsync());
    }

    [Fact]
    public async Task TheThreeBulkTargets_AreAllowed()
    {
        var ids = await AddDomainsAsync("a.example");

        foreach (var target in new[] { "dmarc", "tlsrpt", "mta-sts" })
        {
            Assert.Equal(target, (await CreateBatchAsync(ids, target: target)).Target);
        }
    }

    // ---- Choosing which domains to push ----

    [Fact]
    public async Task ThePersonWhoStartedABatch_CanChooseASubsetOfItsPlannedDomains()
    {
        var ids = await AddDomainsAsync("one.example", "two.example", "three.example");
        var batch = await CreateBatchAsync(ids);

        await using (var context = CreateContext())
        {
            await CreateService().SetIncludedAsync(context, Person, batch.Id, [ids[1]], CancellationToken.None);
        }

        var rows = (await ReloadAsync(batch.Id)).Domains.OrderBy(row => row.Position).ToList();
        Assert.Equal([false, true, false], rows.Select(row => row.Included));
    }

    [Fact]
    public async Task SomeoneElse_CannotChooseForABatch_AndNothingChanges()
    {
        var ids = await AddDomainsAsync("one.example");
        var batch = await CreateBatchAsync(ids);

        await using (var context = CreateContext())
        {
            await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateService().SetIncludedAsync(context, AnotherPerson, batch.Id, [], CancellationToken.None));
        }

        Assert.True(Assert.Single((await ReloadAsync(batch.Id)).Domains).Included);
    }

    [Fact]
    public async Task AnExpiredBatch_CannotBeChanged()
    {
        var ids = await AddDomainsAsync("one.example");
        var batch = await CreateBatchAsync(ids);
        _clock.Advance(TimeSpan.FromMinutes(31));

        await using var context = CreateContext();

        await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateService().SetIncludedAsync(context, Person, batch.Id, [], CancellationToken.None));
    }

    [Fact]
    public async Task ARefusedOrUnchangedDomain_CannotBeIncluded()
    {
        var ids = await AddDomainsAsync("changes.example", "already-correct.example");
        _dmarc.NothingToChange.Add("already-correct.example");
        var batch = await CreateBatchAsync(ids);

        await using var context = CreateContext();

        await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateService().SetIncludedAsync(context, Person, batch.Id, [ids[1]], CancellationToken.None));
    }

    [Fact]
    public async Task ABatchThatIsNoLongerPlanned_CannotBeChanged()
    {
        var ids = await AddDomainsAsync("one.example");
        var batch = await CreateBatchAsync(ids);
        await using var changeContext = CreateContext();
        await changeContext.DnsPushBatches.Include(candidate => candidate.Domains).SingleAsync(candidate => candidate.Id == batch.Id);
        await using (var context = CreateContext())
        {
            await context.DnsPushBatches.Where(candidate => candidate.Id == batch.Id).ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, DnsPushBatchStatus.Running));
        }

        await Assert.ThrowsAsync<DnsPushBatchException>(() => CreateService().SetIncludedAsync(changeContext, Person, batch.Id, [], CancellationToken.None));
        Assert.True(Assert.Single((await ReloadAsync(batch.Id)).Domains).Included);
    }

    [Fact]
    public async Task DeletingABatch_DeletesItsRows()
    {
        var ids = await AddDomainsAsync("one.example", "two.example");
        var batch = await CreateBatchAsync(ids);

        await using (var context = CreateContext())
        {
            await context.DnsPushBatches.Where(candidate => candidate.Id == batch.Id).ExecuteDeleteAsync();
        }

        await using var verify = CreateContext();
        Assert.Empty(await verify.DnsPushBatchDomains.ToListAsync());
    }
}
