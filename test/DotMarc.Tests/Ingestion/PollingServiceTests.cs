using DotMarc.Data;
using DotMarc.Graph;
using DotMarc.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Ingestion;

public class PollingServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dotmarc-polling-test-{Guid.NewGuid()}.db");

    private DotMarcDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        var context = new DotMarcDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private static byte[] GzipOf(string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private const string ValidReportXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <email>noreply-dmarc-support@google.com</email>
            <report_id>1</report_id>
            <date_range><begin>1754438400</begin><end>1754524800</end></date_range>
          </report_metadata>
          <policy_published><domain>contoso.io</domain><adkim>r</adkim><aspf>r</aspf><p>quarantine</p><sp>quarantine</sp><pct>100</pct></policy_published>
          <record>
            <row><source_ip>198.51.100.7</source_ip><count>10</count><policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated></row>
            <identifiers><header_from>contoso.io</header_from></identifiers>
            <auth_results><spf><domain>contoso.io</domain><result>pass</result></spf></auth_results>
          </record>
        </feedback>
        """;

    [Fact]
    public async Task PollOnceAsync_ParsesAndStoresAValidReport_ThenMarksMessageRead()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            var domain = verify.Domains.Include(d => d.Reports).ThenInclude(r => r.Records).Single();
            Assert.Equal("contoso.io", domain.Name);
            Assert.Single(domain.Reports);
            Assert.Single(domain.Reports[0].Records);
        }

        Assert.Contains("msg-1", graphClient.MarkedAsRead);
        Assert.Empty(await CreateContext().ParseFailures.ToListAsync());
    }

    [Fact]
    public async Task PollOnceAsync_RecordsParseFailure_AndLeavesMessageUnread_ForUnparseableAttachment()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-2", "Not a report", true));
        graphClient.Attachments["msg-2"] = [new MailboxAttachment("garbage.xml", "text/xml", "not xml"u8.ToArray())];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            Assert.Empty(verify.Reports);
            var failure = verify.ParseFailures.Single();
            Assert.Equal("msg-2", failure.GraphMessageId);
        }

        Assert.DoesNotContain("msg-2", graphClient.MarkedAsRead);
    }

    [Fact]
    public async Task PollOnceAsync_SkipsMessagesWithNoAttachments()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-3", "Unrelated", false));

        using var context = CreateContext();
        var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(context.Reports);
        Assert.Empty(context.ParseFailures);
        Assert.DoesNotContain("msg-3", graphClient.MarkedAsRead);
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

    private sealed class FakeGraphMailboxClient : IGraphMailboxClient
    {
        public List<MailboxMessage> UnreadMessages { get; } = [];
        public Dictionary<string, List<MailboxAttachment>> Attachments { get; } = [];
        public List<string> MarkedAsRead { get; } = [];

        public Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailboxMessage>>(UnreadMessages);

        public Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailboxAttachment>>(Attachments.GetValueOrDefault(messageId, []));

        public Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken)
        {
            MarkedAsRead.Add(messageId);
            return Task.CompletedTask;
        }
    }
}
