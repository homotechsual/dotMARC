using DotMarc.Data;
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

/// <summary>The builder's queries: only the Group's monitored domains, only the period's reports.</summary>
[Collection("Postgres")]
public sealed class ClientReportBuilderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ClientReportBuilderTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task TheReport_CoversTheGroupsMonitoredDomains_AndOnlyThePeriodsReports()
    {
        int groupId;
        await using (var context = CreateContext())
        {
            var aurora = new Group { Name = "Aurora Retail" };
            var other = new Group { Name = "Brightline Legal" };
            var inGroup = new Domain { Name = "aurora-retail.example", IsMonitored = true, Groups = [aurora] };
            var unmonitored = new Domain { Name = "old.aurora-retail.example", IsMonitored = false, Groups = [aurora] };
            var elsewhere = new Domain { Name = "brightline-legal.example", IsMonitored = true, Groups = [other] };
            context.Domains.AddRange(inGroup, unmonitored, elsewhere);
            await context.SaveChangesAsync();
            context.Reports.AddRange(
                Report(inGroup.Id, new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), 40),
                Report(inGroup.Id, new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero), 999));
            await context.SaveChangesAsync();
            groupId = aurora.Id;
        }

        var builder = new ClientReportBuilder(new FakeDbContextFactory(_connectionString), new PortalBrandLoader(new FakeDbContextFactory(_connectionString)),
            new FixedTimeProvider(new DateTimeOffset(2026, 4, 3, 0, 0, 0, TimeSpan.Zero)));
        var report = await builder.BuildAsync(groupId, new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month), TimeZoneInfo.Utc, CancellationToken.None);

        var domain = Assert.Single(report!.Domains);
        Assert.Equal(("aurora-retail.example", 40L, "Aurora Retail"), (domain.Name, domain.Messages, report.Brand.Heading));
    }

    [Fact]
    public async Task TheReport_UsesTheSavedNumberFormat()
    {
        int groupId;
        await using (var context = CreateContext())
        {
            var group = new Group { Name = "Aurora Retail" };
            context.Groups.Add(group);
            (await context.ReportSettings.SingleAsync()).NumberFormat = "de-DE";
            await context.SaveChangesAsync();
            groupId = group.Id;
        }

        var builder = new ClientReportBuilder(new FakeDbContextFactory(_connectionString), new PortalBrandLoader(new FakeDbContextFactory(_connectionString)), TimeProvider.System);
        var report = await builder.BuildAsync(groupId, new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month), TimeZoneInfo.Utc, CancellationToken.None);

        Assert.Equal("de-DE", report!.NumberFormat);
    }

    [Fact]
    public async Task AGroupThatDoesntExist_HasNoReport()
    {
        var builder = new ClientReportBuilder(new FakeDbContextFactory(_connectionString), new PortalBrandLoader(new FakeDbContextFactory(_connectionString)), TimeProvider.System);

        Assert.Null(await builder.BuildAsync(424242, new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month), TimeZoneInfo.Utc, CancellationToken.None));
    }

    private static Report Report(int domainId, DateTimeOffset beginUtc, int messages) => new()
    {
        DomainId = domainId, ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = beginUtc,
        DateRangeEndUtc = beginUtc.AddDays(1), ReceivedUtc = beginUtc.AddDays(1), RawXml = "<feedback/>",
        Records = [new ReportRecord { SourceIp = "203.0.113.10", MessageCount = messages, SpfResult = AuthResult.Pass, DkimResult = AuthResult.Pass, HeaderFrom = "aurora-retail.example" }],
    };
}
