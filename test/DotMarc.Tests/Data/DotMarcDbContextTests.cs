using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

public sealed class DotMarcDbContextTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dotmarc-test-{Guid.NewGuid()}.db");

    private DotMarcDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        var context = new DotMarcDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public void CanInsertAndQuery_DomainWithReportAndRecords()
    {
        using (var context = CreateContext())
        {
            var domain = new Domain
            {
                Name = "contoso.io",
                IsPinned = true,
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
            Assert.True(savedDomain.IsPinned);
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

    public void Dispose()
    {
        // Ensure all connections are closed
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Delete main database file and SQLite WAL files
        foreach (var path in new[] { _dbPath, _dbPath + "-shm", _dbPath + "-wal" })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Ignore if still locked - will be cleaned up by OS
                }
            }
        }
    }
}
