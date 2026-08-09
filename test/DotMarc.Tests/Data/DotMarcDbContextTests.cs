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
