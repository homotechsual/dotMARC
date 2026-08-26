using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class DotMarcDbContextTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DotMarcDbContextTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public void CanInsertAndQuery_DomainWithReportAndRecords()
    {
        using (var context = CreateContext())
        {
            var domain = new Domain
            {
                Name = "contoso.io",
                IsMonitored = true,
                FirstSeenUtc = DateTimeOffset.UtcNow
            };
            var report = new Report
            {
                Domain = domain,
                ReportingOrg = "google.com",
                ReportId = "123",
                DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                DateRangeEndUtc = DateTimeOffset.UtcNow,
                RawXml = "<feedback/>",
                ReceivedUtc = DateTimeOffset.UtcNow
            };
            report.Records.Add(new ReportRecord
            {
                SourceIp = "198.51.100.7",
                MessageCount = 10,
                Disposition = DispositionResult.None,
                SpfResult = AuthResult.Pass,
                DkimResult = AuthResult.Pass,
                HeaderFrom = "contoso.io"
            });

            context.Domains.Add(domain);
            context.Reports.Add(report);
            context.SaveChanges();
        }

        using (var verifyContext = CreateContext())
        {
            var savedDomain = verifyContext.Domains
                .Include(d => d.Reports)
                .ThenInclude(r => r.Records)
                .Single();

            Assert.Equal("contoso.io", savedDomain.Name);
            Assert.True(savedDomain.IsMonitored);
            Assert.Single(savedDomain.Reports);
            Assert.Single(savedDomain.Reports[0].Records);
            Assert.Equal(AuthResult.Pass, savedDomain.Reports[0].Records[0].SpfResult);
            Assert.Equal(DispositionResult.None, savedDomain.Reports[0].Records[0].Disposition);
        }
    }

    [Fact]
    public void DomainName_MustBeUnique()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Report_DomainReportingOrgReportId_MustBeUnique()
    {
        // Backs PollingService's idempotency fix: without this constraint, reprocessing a message
        // whose report was already stored (e.g. because MarkAsReadAsync failed after the report
        // was saved) would silently double-count volume instead of being caught.
        using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        context.Reports.Add(new Report
        {
            Domain = domain,
            ReportingOrg = "google.com",
            ReportId = "dup-1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow
        });
        context.SaveChanges();

        context.Reports.Add(new Report
        {
            Domain = domain,
            ReportingOrg = "google.com",
            ReportId = "dup-1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow
        });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void ParseFailure_GraphMessageId_MustBeUnique()
    {
        using var context = CreateContext();
        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "msg-1", Reason = "bad xml", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "msg-1", Reason = "bad xml again", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void ChangeTrackerClear_RemovesEntitiesLeftDanglingByAFailedSaveChanges()
    {
        // Regression coverage for the review finding: PollingService shares one DbContext across
        // a whole poll cycle. If a mid-cycle SaveChangesAsync throws (e.g. a constraint
        // violation), the half-built entities from that failed call stay tracked as Added unless
        // the tracker is explicitly cleared — otherwise the *next* SaveChanges call (recording a
        // ParseFailure) re-attempts them too and can throw again, uncaught. This confirms the
        // assumption PollingService's fix relies on: ChangeTracker.Clear() actually drops the
        // dangling entities, and a subsequent unrelated save then succeeds.
        using var context = CreateContext();
        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "dangling", Reason = "first", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });
        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "dangling", Reason = "second", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.NotEmpty(context.ChangeTracker.Entries());

        context.ChangeTracker.Clear();
        Assert.Empty(context.ChangeTracker.Entries());

        // A subsequent, unrelated save now succeeds instead of re-attempting the dangling inserts.
        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "unrelated", Reason = "ok", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        Assert.Equal(0, context.ParseFailures.Count(f => f.GraphMessageId == "dangling"));
        Assert.Equal(1, context.ParseFailures.Count(f => f.GraphMessageId == "unrelated"));
    }

    [Fact]
    public void CanInsertAndQuery_PollCycle()
    {
        using (var context = CreateContext())
        {
            context.PollCycles.Add(new PollCycle
            {
                PolledUtc = DateTimeOffset.UtcNow,
                MessagesChecked = 4,
                ReportsParsed = 3,
                ParseFailures = 1,
                Succeeded = true
            });
            context.SaveChanges();
        }

        using (var verify = CreateContext())
        {
            var pollCycle = verify.PollCycles.Single();
            Assert.Equal(4, pollCycle.MessagesChecked);
            Assert.Equal(3, pollCycle.ReportsParsed);
            Assert.Equal(1, pollCycle.ParseFailures);
            Assert.True(pollCycle.Succeeded);
            Assert.Null(pollCycle.ErrorMessage);
        }
    }

    [Fact]
    public void PollCycleDailySummary_DateMustBeUnique()
    {
        using var context = CreateContext();
        context.PollCycleDailySummaries.Add(new PollCycleDailySummary { Date = new DateOnly(2026, 8, 1) });
        context.SaveChanges();

        context.PollCycleDailySummaries.Add(new PollCycleDailySummary { Date = new DateOnly(2026, 8, 1) });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void CanInsertAndQuery_DomainWithDmarcCheckFields()
    {
        using (var context = CreateContext())
        {
            context.Domains.Add(new Domain
            {
                Name = "contoso.io",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                DmarcCheckStatus = DmarcCheckStatus.MissingAuthorizationRecord,
                DmarcCheckedUtc = DateTimeOffset.UtcNow,
                DmarcCheckDetail = "No TXT record found at contoso.io._report._dmarc.mjco.uk"
            });
            context.SaveChanges();
        }

        using (var verify = CreateContext())
        {
            var domain = verify.Domains.Single();
            Assert.Equal(DmarcCheckStatus.MissingAuthorizationRecord, domain.DmarcCheckStatus);
            Assert.NotNull(domain.DmarcCheckedUtc);
            Assert.Equal("No TXT record found at contoso.io._report._dmarc.mjco.uk", domain.DmarcCheckDetail);
        }
    }

    [Fact]
    public void Domain_DmarcCheckStatus_DefaultsToNotChecked()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        using var verify = CreateContext();
        var domain = verify.Domains.Single();
        Assert.Equal(DmarcCheckStatus.NotChecked, domain.DmarcCheckStatus);
        Assert.Null(domain.DmarcCheckedUtc);
        Assert.Null(domain.DmarcCheckDetail);
    }
}
