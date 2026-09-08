using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class AuthDetailBackfillCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuthDetailBackfillCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static PollingService CreateService(DotMarcDbContext context) =>
        new(new FakeGraphMailboxClient(), context, NullLogger<PollingService>.Instance);

    private const string RawXmlWithDetail = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <email>noreply-dmarc-support@google.com</email>
            <report_id>backfill-1</report_id>
            <date_range><begin>1754438400</begin><end>1754524800</end></date_range>
          </report_metadata>
          <policy_published><domain>contoso.io</domain><adkim>r</adkim><aspf>r</aspf><p>quarantine</p><sp>quarantine</sp><pct>100</pct></policy_published>
          <record>
            <row>
              <source_ip>203.0.113.60</source_ip>
              <count>5</count>
              <policy_evaluated>
                <disposition>reject</disposition><dkim>fail</dkim><spf>fail</spf>
                <reason><type>other</type></reason>
              </policy_evaluated>
            </row>
            <identifiers><header_from>contoso.io</header_from></identifiers>
            <auth_results>
              <spf><domain>contoso.io</domain><result>fail</result></spf>
            </auth_results>
          </record>
        </feedback>
        """;

    private async Task<int> SeedUnbackfilledReportAsync()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        var report = new Report
        {
            ReportingOrg = "google.com",
            ReportId = "backfill-1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = RawXmlWithDetail,
            ReceivedUtc = DateTimeOffset.UtcNow,
            AuthDetailBackfilledUtc = null,
            Records = { new ReportRecord { SourceIp = "203.0.113.60", MessageCount = 5, HeaderFrom = "contoso.io", Disposition = DispositionResult.Reject, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail } }
        };
        domain.Reports.Add(report);
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return report.Id;
    }

    [Fact]
    public async Task RunAuthDetailBackfillCycleAsync_PopulatesDetailForAnUnbackfilledReport_AndMarksItBackfilled()
    {
        var reportId = await SeedUnbackfilledReportAsync();

        using var context = CreateContext();
        var service = CreateService(context);
        await service.RunAuthDetailBackfillCycleAsync(context, CancellationToken.None);

        using var verify = CreateContext();
        var report = verify.Reports
            .Include(r => r.Records).ThenInclude(rec => rec.AuthDetails)
            .Include(r => r.Records).ThenInclude(rec => rec.OverrideReasons)
            .Single(r => r.Id == reportId);

        Assert.NotNull(report.AuthDetailBackfilledUtc);
        var record = report.Records.Single();
        Assert.Single(record.AuthDetails);
        Assert.Equal(DmarcMechanismResult.Fail, record.AuthDetails[0].Result);
        Assert.Single(record.OverrideReasons);
        Assert.Equal(DmarcPolicyOverrideType.Other, record.OverrideReasons[0].Type);
    }

    [Fact]
    public async Task RunAuthDetailBackfillCycleAsync_SkipsAReportAlreadyBackfilled()
    {
        await using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
            domain.Reports.Add(new Report
            {
                ReportingOrg = "google.com",
                ReportId = "already-done",
                DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                DateRangeEndUtc = DateTimeOffset.UtcNow,
                RawXml = RawXmlWithDetail,
                ReceivedUtc = DateTimeOffset.UtcNow,
                AuthDetailBackfilledUtc = DateTimeOffset.UtcNow,
                Records = { new ReportRecord { SourceIp = "203.0.113.61", MessageCount = 1, HeaderFrom = "contoso.io" } }
            });
            context.Domains.Add(domain);
            await context.SaveChangesAsync();
        }

        using var context2 = CreateContext();
        var service = CreateService(context2);
        await service.RunAuthDetailBackfillCycleAsync(context2, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.ReportRecordAuthDetails);
    }

    [Fact]
    public async Task RunAuthDetailBackfillCycleAsync_SkipsAndMarksAReportWhoseRawXmlNoLongerParses_WithoutStoppingTheBatch()
    {
        int goodReportId;
        await using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
            var badReport = new Report
            {
                ReportingOrg = "google.com",
                ReportId = "bad-xml",
                DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                DateRangeEndUtc = DateTimeOffset.UtcNow,
                RawXml = "not valid xml at all",
                ReceivedUtc = DateTimeOffset.UtcNow,
                AuthDetailBackfilledUtc = null,
                Records = { new ReportRecord { SourceIp = "203.0.113.62", MessageCount = 1, HeaderFrom = "contoso.io" } }
            };
            var goodReport = new Report
            {
                ReportingOrg = "google.com",
                ReportId = "backfill-1",
                DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                DateRangeEndUtc = DateTimeOffset.UtcNow,
                RawXml = RawXmlWithDetail,
                ReceivedUtc = DateTimeOffset.UtcNow,
                AuthDetailBackfilledUtc = null,
                Records = { new ReportRecord { SourceIp = "203.0.113.60", MessageCount = 5, HeaderFrom = "contoso.io" } }
            };
            domain.Reports.Add(badReport);
            domain.Reports.Add(goodReport);
            context.Domains.Add(domain);
            await context.SaveChangesAsync();
            goodReportId = goodReport.Id;
        }

        using var context2 = CreateContext();
        var service = CreateService(context2);
        await service.RunAuthDetailBackfillCycleAsync(context2, CancellationToken.None);

        using var verify = CreateContext();
        var goodReport2 = verify.Reports.Include(r => r.Records).ThenInclude(rec => rec.AuthDetails).Single(r => r.Id == goodReportId);
        Assert.NotEmpty(goodReport2.Records.Single().AuthDetails);

        var badReport2 = verify.Reports.Single(r => r.ReportId == "bad-xml");
        Assert.NotNull(badReport2.AuthDetailBackfilledUtc); // marked handled so it isn't retried forever
    }

    [Fact]
    public async Task RunAuthDetailBackfillCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        await SeedUnbackfilledReportAsync();

        using var context = CreateContext();
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.AuthDetailBackfillLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var service = CreateService(context);
        await service.RunAuthDetailBackfillCycleAsync(context, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.ReportRecordAuthDetails);

        await lockTransaction.RollbackAsync();
    }
}
