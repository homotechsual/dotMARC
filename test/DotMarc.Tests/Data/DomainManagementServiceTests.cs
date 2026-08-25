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
        Assert.True(domain.IsPinned);
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
    public async Task SetPinnedAsync_TogglesIsPinned()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetPinnedAsync(context, domainId, false, CancellationToken.None);

        using var verify = CreateContext();
        Assert.False(verify.Domains.Single().IsPinned);
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
}
