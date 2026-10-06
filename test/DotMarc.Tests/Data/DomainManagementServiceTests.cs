using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class DomainManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DomainManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DotMarcDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new DotMarcDbContext(options);
    }

    [Fact]
    public async Task AddDomainAsync_CreatesAPinnedDomain_WithNormalizedName()
    {
        using var context = CreateContext();

        var result = await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "Contoso.COM", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.Added, result);
        var domain = context.Domains.Single();
        Assert.Equal("contoso.com", domain.Name);
        Assert.True(domain.IsMonitored);
        Assert.Null(domain.LastReportReceivedUtc);
    }

    [Fact]
    public async Task AddDomainAsync_RejectsInvalidName()
    {
        using var context = CreateContext();

        var result = await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "not-a-domain", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.InvalidName, result);
        Assert.Empty(context.Domains);
    }

    [Fact]
    public async Task AddDomainAsync_RejectsDuplicate_RegardlessOfCasing()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com", CancellationToken.None);

        var result = await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "CONTOSO.com", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.AlreadyMonitored, result);
        Assert.Single(context.Domains);
    }

    [Fact]
    public async Task RemoveDomainAsync_DeletesDomainWithNoReports()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.RemoveDomainAsync(context, TestActors.Admin, domainId, CancellationToken.None);

        Assert.Empty(context.Domains);
    }

    [Fact]
    public async Task RemoveDomainAsync_CascadesReportsAndRecords()
    {
        using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        var report = new Report
        {
            Domain = domain,
            ReportingOrg = "google.com",
            ReportId = "1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow
        };
        report.Records.Add(new ReportRecord
        {
            SourceIp = "198.51.100.7",
            MessageCount = 5,
            Disposition = DispositionResult.None,
            SpfResult = AuthResult.Pass,
            DkimResult = AuthResult.Pass,
            HeaderFrom = "contoso.io"
        });
        context.Domains.Add(domain);
        context.Reports.Add(report);
        await context.SaveChangesAsync();

        await DomainManagementService.RemoveDomainAsync(context, TestActors.Admin, domain.Id, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.Domains);
        Assert.Empty(verify.Reports);
        Assert.Empty(verify.ReportRecords);
    }

    [Fact]
    public async Task SetMonitoredAsync_TogglesIsMonitored()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetMonitoredAsync(context, TestActors.Admin, domainId, false, CancellationToken.None);

        using var verify = CreateContext();
        Assert.False(verify.Domains.Single().IsMonitored);
    }

    [Fact]
    public async Task SetMtaStsConfigAsync_SavesConfig_AndResetsStatusToPendingDns_WhenEnablingForTheFirstTime()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetMtaStsConfigAsync(
            context, TestActors.Admin, domainId, enabled: true, MtaStsMode.Enforce, ["mail.contoso.com"], 86_400, CancellationToken.None);

        using var verify = CreateContext();
        var domain = verify.Domains.Single();
        Assert.True(domain.MtaStsEnabled);
        Assert.Equal(MtaStsStatus.PendingDns, domain.MtaStsStatus);
        Assert.Equal(MtaStsMode.Enforce, domain.MtaStsMode);
        Assert.Equal(["mail.contoso.com"], domain.MtaStsMxHosts);
        Assert.Equal(86_400, domain.MtaStsMaxAgeSeconds);
    }

    [Fact]
    public async Task SetMtaStsConfigAsync_RefusesToTurnOnWithNoMxHosts_BecauseThePolicyWouldListNoMailServers()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await Assert.ThrowsAsync<ArgumentException>(() => DomainManagementService.SetMtaStsConfigAsync(
            context, TestActors.Admin, domainId, enabled: true, MtaStsMode.Testing, [], 604_800, CancellationToken.None));

        using var verify = CreateContext();
        Assert.False(verify.Domains.Single().MtaStsEnabled);
    }

    [Fact]
    public async Task SetMtaStsConfigAsync_LetsHostingBeOffWithNoMxHosts()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetMtaStsConfigAsync(
            context, TestActors.Admin, domainId, enabled: false, MtaStsMode.Testing, [], 86_400, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(86_400, verify.Domains.Single().MtaStsMaxAgeSeconds);
    }

    [Fact]
    public async Task SetMtaStsConfigAsync_LeavesStatusAlone_WhenAlreadyEnabled()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        await DomainManagementService.SetMtaStsConfigAsync(
            context, TestActors.Admin, domainId, enabled: true, MtaStsMode.Testing, ["mail.contoso.com"], 604_800, CancellationToken.None);
        context.Domains.Single().MtaStsStatus = MtaStsStatus.Active;
        await context.SaveChangesAsync();

        // Editing the MX list on an already-enabled, already-Active domain shouldn't reset it back
        // to PendingDns - only the false-to-true enable transition does that.
        await DomainManagementService.SetMtaStsConfigAsync(
            context, TestActors.Admin, domainId, enabled: true, MtaStsMode.Testing, ["mail.contoso.com", "backup.contoso.com"], 604_800, CancellationToken.None);

        using var verify = CreateContext();
        var domain = verify.Domains.Single();
        Assert.Equal(MtaStsStatus.Active, domain.MtaStsStatus);
        Assert.Equal(["mail.contoso.com", "backup.contoso.com"], domain.MtaStsMxHosts);
    }

    [Fact]
    public async Task DbUpdateException_FromAUniqueViolation_WrapsAPostgresExceptionWithSqlState23505()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        context.Domains.Add(new Domain { Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var pgEx = Assert.IsType<Npgsql.PostgresException>(ex.InnerException);
        Assert.Equal("23505", pgEx.SqlState);
    }

    [Fact]
    public async Task ReorderAsync_SetsSortOrderToMatchTheGivenSequence()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "a.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "b.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "c.com", CancellationToken.None);

        var domains = context.Domains.OrderBy(d => d.Name).ToList();
        var a = domains.Single(d => d.Name == "a.com");
        var b = domains.Single(d => d.Name == "b.com");
        var c = domains.Single(d => d.Name == "c.com");

        await DomainManagementService.ReorderAsync(context, TestActors.Admin, [c.Id, a.Id, b.Id], CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(0, verify.Domains.Single(d => d.Name == "c.com").SortOrder);
        Assert.Equal(1, verify.Domains.Single(d => d.Name == "a.com").SortOrder);
        Assert.Equal(2, verify.Domains.Single(d => d.Name == "b.com").SortOrder);
    }

    [Fact]
    public async Task AddDomainAsync_AppendsToTheEnd_WhenOtherDomainsAlreadyHaveDistinctSortOrder()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "a.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "b.com", CancellationToken.None);
        var existing = context.Domains.OrderBy(d => d.Name).ToList();
        await DomainManagementService.ReorderAsync(context, TestActors.Admin, [existing[1].Id, existing[0].Id], CancellationToken.None);

        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "c.com", CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(2, verify.Domains.Single(d => d.Name == "c.com").SortOrder);
    }

    [Fact]
    public void DomainsWithTiedSortOrder_SortByNameAsTheSecondaryKey()
    {
        // Regression coverage for "existing installs don't need a data-backfill migration": rows
        // created directly (bypassing AddDomainAsync's append-at-end logic), the way every domain
        // that predates this feature exists today, are left at SortOrder's default of 0 - tied.
        // The ordering query's secondary key must still produce a sensible, predictable order.
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "zebra.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.Domains.Add(new Domain { Name = "apple.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.Domains.Add(new Domain { Name = "mango.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        var ordered = context.Domains.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => d.Name).ToList();

        Assert.Equal(["apple.com", "mango.com", "zebra.com"], ordered);
    }

    [Fact]
    public async Task SetHaloClientIdAsync_UpdatesTheDomainsOverride()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();

        await DomainManagementService.SetHaloClientIdAsync(context, TestActors.Admin, domain.Id, 7);

        await using var verify = CreateContext();
        Assert.Equal(7, (await verify.Domains.SingleAsync(d => d.Id == domain.Id)).HaloClientId);
    }

    [Fact]
    public async Task SetDkimSelectorsAsync_SavesTheSelectorList()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetDkimSelectorsAsync(context, TestActors.Admin, domainId, ["selector1", "selector2"], CancellationToken.None);

        using var verify = CreateContext();
        var domain = verify.Domains.Single();
        Assert.Equal(["selector1", "selector2"], domain.DkimSelectors);
    }

    private static async Task<AuditEntry> LatestEntryAsync(DotMarcDbContext context) =>
        await context.AuditEntries.OrderByDescending(entry => entry.Id).FirstAsync();

    [Fact]
    public async Task AddDomainAsync_RecordsWhoAddedTheDomain()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "Contoso.com");
        }

        await using var verify = CreateContext();
        var domain = await verify.Domains.SingleAsync();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.DomainAdded, entry.Action);
        Assert.Equal(AuditEntryKind.Change, entry.Kind);
        Assert.Equal("admin@example.com", entry.ActorEmail);
        Assert.Equal(("Domain", domain.Id.ToString(), "contoso.com"), (entry.TargetType, entry.TargetId, entry.TargetName));
        Assert.Equal("Added domain contoso.com", entry.Summary);
    }

    [Fact]
    public async Task AddDomainAsync_RecordsNothing_WhenItRefusesTheDomain()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "not a domain");
        }

        await using var verify = CreateContext();
        Assert.Single(verify.AuditEntries);
    }

    [Fact]
    public async Task SetMonitoredAsync_RecordsTheOldAndNewValue()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await DomainManagementService.SetMonitoredAsync(context, TestActors.Admin, domainId, isMonitored: false);
        }

        await using var verify = CreateContext();
        var entry = await LatestEntryAsync(verify);
        Assert.Equal(AuditActions.DomainMonitoringChanged, entry.Action);
        Assert.Equal("Stopped monitoring contoso.com", entry.Summary);
        Assert.Equal([new AuditFieldChange("Monitored", "Yes", "No")], entry.Changes);
    }

    [Fact]
    public async Task SetMonitoredAsync_RecordsNothing_WhenTheValueIsUnchanged()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await DomainManagementService.SetMonitoredAsync(context, TestActors.Admin, domainId, isMonitored: true);
        }

        await using var verify = CreateContext();
        Assert.Single(verify.AuditEntries);
    }

    [Fact]
    public async Task SetMtaStsConfigAsync_RecordsOnlyTheFieldsThatChanged()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await DomainManagementService.SetMtaStsConfigAsync(context, TestActors.Admin, domainId, enabled: true, MtaStsMode.Testing, ["mx1.contoso.com"], 86400);
            await DomainManagementService.SetMtaStsConfigAsync(context, TestActors.Admin, domainId, enabled: true, MtaStsMode.Enforce, ["mx1.contoso.com"], 86400);
        }

        await using var verify = CreateContext();
        var entry = await LatestEntryAsync(verify);
        Assert.Equal(AuditActions.DomainMtaStsChanged, entry.Action);
        Assert.Equal([new AuditFieldChange("Mode", "Testing", "Enforce")], entry.Changes);
    }

    [Fact]
    public async Task ReorderAsync_RecordsOneEntryWithTheOldAndNewOrder()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "alpha.com");
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "beta.com");
            var domainIdsByName = await context.Domains.ToDictionaryAsync(domain => domain.Name, domain => domain.Id);
            await DomainManagementService.ReorderAsync(context, TestActors.Admin, [domainIdsByName["beta.com"], domainIdsByName["alpha.com"]]);
        }

        await using var verify = CreateContext();
        var reorderEntry = await verify.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.DomainsReordered);
        Assert.Equal([new AuditFieldChange("Order", "alpha.com, beta.com", "beta.com, alpha.com")], reorderEntry.Changes);
    }

    [Fact]
    public async Task RemoveDomainAsync_RecordsTheRemovedDomainsName()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await DomainManagementService.RemoveDomainAsync(context, TestActors.Admin, domainId);
        }

        await using var verify = CreateContext();
        var entry = await LatestEntryAsync(verify);
        Assert.Equal((AuditActions.DomainRemoved, "contoso.com", "Removed domain contoso.com"), (entry.Action, entry.TargetName, entry.Summary));
    }

    private async Task<int> SeedDomainWithSelectorsAsync(params string[] selectors)
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "dkim.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, DkimSelectors = [.. selectors] };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    [Fact]
    public async Task SetDkimRecordsAsync_SavesTidiedValues_AndAuditsThem()
    {
        var domainId = await SeedDomainWithSelectorsAsync("selector1", "google");

        await using (var context = CreateContext())
        {
            await DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId,
            [
                new DkimRecordInput("selector1", DkimRecordType.Cname, "Selector1-dkim-example._domainkey.contoso.onmicrosoft.com."),
                new DkimRecordInput("google", DkimRecordType.Txt, "\"v=DKIM1; k=rsa; \" \"p=MIIBIjAN\""),
                new DkimRecordInput("not-a-selector", DkimRecordType.Txt, "v=DKIM1; p=IGNORED"),
            ]);
        }

        await using var verify = CreateContext();
        var records = await verify.DomainDkimRecords.OrderBy(record => record.Selector).ToListAsync();
        Assert.Equal(
            [("google", DkimRecordType.Txt, "v=DKIM1; k=rsa; p=MIIBIjAN"), ("selector1", DkimRecordType.Cname, "selector1-dkim-example._domainkey.contoso.onmicrosoft.com")],
            records.Select(record => (record.Selector, record.RecordType, record.Value)));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.DomainDkimRecordsChanged, entry.Action);
        Assert.Contains(new AuditFieldChange("DKIM selector1", null, "CNAME selector1-dkim-example._domainkey.contoso.onmicrosoft.com"), entry.Changes);
    }

    [Fact]
    public async Task SetDkimRecordsAsync_ABlankValueRemovesTheRecord()
    {
        var domainId = await SeedDomainWithSelectorsAsync("google");
        await using (var context = CreateContext())
        {
            await DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId, [new DkimRecordInput("google", DkimRecordType.Txt, "v=DKIM1; p=ABC")]);
            await DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId, [new DkimRecordInput("google", DkimRecordType.Txt, "  ")]);
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.DomainDkimRecords);
    }

    [Fact]
    public async Task SetDkimRecordsAsync_RefusesAnInvalidValue_NamingTheSelector()
    {
        var domainId = await SeedDomainWithSelectorsAsync("google");
        await using var context = CreateContext();

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() =>
            DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId, [new DkimRecordInput("google", DkimRecordType.Txt, "v=DKIM1; k=rsa")]));

        Assert.StartsWith("google:", refusal.Message);
        Assert.DoesNotContain("Parameter", refusal.Message);
    }

    [Fact]
    public async Task SetDkimSelectorsAsync_RemovesTheRecordsOfRemovedSelectors()
    {
        var domainId = await SeedDomainWithSelectorsAsync("selector1", "selector2");
        await using (var context = CreateContext())
        {
            await DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId,
            [
                new DkimRecordInput("selector1", DkimRecordType.Txt, "v=DKIM1; p=ONE"),
                new DkimRecordInput("selector2", DkimRecordType.Txt, "v=DKIM1; p=TWO"),
            ]);
            await DomainManagementService.SetDkimSelectorsAsync(context, TestActors.Admin, domainId, ["selector1"]);
        }

        await using var verify = CreateContext();
        Assert.Equal(["selector1"], await verify.DomainDkimRecords.Select(record => record.Selector).ToListAsync());
    }
}
