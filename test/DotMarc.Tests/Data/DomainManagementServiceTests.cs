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

        var result = await DomainManagementService.AddDomainAsync(context, "Contoso.COM", CancellationToken.None);

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

        var result = await DomainManagementService.AddDomainAsync(context, "not-a-domain", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.InvalidName, result);
        Assert.Empty(context.Domains);
    }

    [Fact]
    public async Task AddDomainAsync_RejectsDuplicate_RegardlessOfCasing()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);

        var result = await DomainManagementService.AddDomainAsync(context, "CONTOSO.com", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.AlreadyMonitored, result);
        Assert.Single(context.Domains);
    }

    [Fact]
    public async Task RemoveDomainAsync_DeletesDomainWithNoReports()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.RemoveDomainAsync(context, domainId, CancellationToken.None);

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

        await DomainManagementService.RemoveDomainAsync(context, domain.Id, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.Domains);
        Assert.Empty(verify.Reports);
        Assert.Empty(verify.ReportRecords);
    }

    [Fact]
    public async Task SetMonitoredAsync_TogglesIsMonitored()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetMonitoredAsync(context, domainId, false, CancellationToken.None);

        using var verify = CreateContext();
        Assert.False(verify.Domains.Single().IsMonitored);
    }

    [Fact]
    public async Task SetMtaStsConfigAsync_SavesConfig_AndResetsStatusToPendingDns_WhenEnablingForTheFirstTime()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetMtaStsConfigAsync(
            context, domainId, enabled: true, MtaStsMode.Enforce, ["mail.contoso.com"], 86_400, CancellationToken.None);

        using var verify = CreateContext();
        var domain = verify.Domains.Single();
        Assert.True(domain.MtaStsEnabled);
        Assert.Equal(MtaStsStatus.PendingDns, domain.MtaStsStatus);
        Assert.Equal(MtaStsMode.Enforce, domain.MtaStsMode);
        Assert.Equal(["mail.contoso.com"], domain.MtaStsMxHosts);
        Assert.Equal(86_400, domain.MtaStsMaxAgeSeconds);
    }

    [Fact]
    public async Task SetMtaStsConfigAsync_LeavesStatusAlone_WhenAlreadyEnabled()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        await DomainManagementService.SetMtaStsConfigAsync(
            context, domainId, enabled: true, MtaStsMode.Testing, ["mail.contoso.com"], 604_800, CancellationToken.None);
        context.Domains.Single().MtaStsStatus = MtaStsStatus.Active;
        await context.SaveChangesAsync();

        // Editing the MX list on an already-enabled, already-Active domain shouldn't reset it back
        // to PendingDns — only the false-to-true enable transition does that.
        await DomainManagementService.SetMtaStsConfigAsync(
            context, domainId, enabled: true, MtaStsMode.Testing, ["mail.contoso.com", "backup.contoso.com"], 604_800, CancellationToken.None);

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
        await DomainManagementService.AddDomainAsync(context, "a.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, "b.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, "c.com", CancellationToken.None);

        var domains = context.Domains.OrderBy(d => d.Name).ToList();
        var a = domains.Single(d => d.Name == "a.com");
        var b = domains.Single(d => d.Name == "b.com");
        var c = domains.Single(d => d.Name == "c.com");

        await DomainManagementService.ReorderAsync(context, [c.Id, a.Id, b.Id], CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(0, verify.Domains.Single(d => d.Name == "c.com").SortOrder);
        Assert.Equal(1, verify.Domains.Single(d => d.Name == "a.com").SortOrder);
        Assert.Equal(2, verify.Domains.Single(d => d.Name == "b.com").SortOrder);
    }

    [Fact]
    public async Task AddDomainAsync_AppendsToTheEnd_WhenOtherDomainsAlreadyHaveDistinctSortOrder()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "a.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, "b.com", CancellationToken.None);
        var existing = context.Domains.OrderBy(d => d.Name).ToList();
        await DomainManagementService.ReorderAsync(context, [existing[1].Id, existing[0].Id], CancellationToken.None);

        await DomainManagementService.AddDomainAsync(context, "c.com", CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(2, verify.Domains.Single(d => d.Name == "c.com").SortOrder);
    }

    [Fact]
    public void DomainsWithTiedSortOrder_SortByNameAsTheSecondaryKey()
    {
        // Regression coverage for "existing installs don't need a data-backfill migration": rows
        // created directly (bypassing AddDomainAsync's append-at-end logic), the way every domain
        // that predates this feature exists today, are left at SortOrder's default of 0 — tied.
        // The ordering query's secondary key must still produce a sensible, predictable order.
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "zebra.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.Domains.Add(new Domain { Name = "apple.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.Domains.Add(new Domain { Name = "mango.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        var ordered = context.Domains.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => d.Name).ToList();

        Assert.Equal(["apple.com", "mango.com", "zebra.com"], ordered);
    }
}
