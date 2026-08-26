using DotMarc.Data;
using DotMarc.Graph;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public class PollingServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PollingServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task PollOnceAsync_DoesNotDuplicateReport_WhenMarkAsReadFailsAfterStoreSucceeds_AndMessageIsReprocessed()
    {
        // Regression coverage for the review finding: the report is stored, but MarkAsReadAsync
        // (a separate, transient Graph call) fails, so the message stays unread and gets
        // reprocessed on the next poll. That second attempt must not create a second Report row
        // for the same (domain, reporting org, report id).
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];
        graphClient.FailMarkAsReadFor.Add("msg-1");

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        // First attempt: report stored, but marking read failed — so it's NOT a ParseFailure, and
        // the message is still considered unread for the next poll.
        using (var verify = CreateContext())
        {
            Assert.Single(verify.Reports);
            Assert.Empty(verify.ParseFailures);
        }
        Assert.DoesNotContain("msg-1", graphClient.MarkedAsRead);

        // Second poll: Graph now succeeds at marking read. The same message (still unread, still
        // carrying the same report) is reprocessed.
        graphClient.FailMarkAsReadFor.Clear();
        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            Assert.Single(verify.Reports); // still exactly one — no duplicate.
            Assert.Empty(verify.ParseFailures);
        }
        Assert.Contains("msg-1", graphClient.MarkedAsRead);
    }

    [Fact]
    public async Task PollOnceAsync_UpdatesExistingParseFailureRow_InsteadOfGrowingUnboundedly_OnRepeatedFailure()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-2", "Not a report", true));
        graphClient.Attachments["msg-2"] = [new MailboxAttachment("garbage.xml", "text/xml", "not xml"u8.ToArray())];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
            await service.PollOnceAsync(CancellationToken.None);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using var verify = CreateContext();
        var failure = verify.ParseFailures.Single();
        Assert.Equal("msg-2", failure.GraphMessageId);
        Assert.Equal(3, failure.AttemptCount);
    }

    [Fact]
    public async Task PollOnceAsync_MatchesAManuallyAddedDomain_InsteadOfCreatingADuplicate()
    {
        // Regression coverage for the "manage domains" feature: a domain added up front (before
        // any report has arrived for it) must be picked up by its Name when the first real report
        // lands, not treated as unseen and duplicated.
        using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, "contoso.io", CancellationToken.None);
        }

        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using var verify = CreateContext();
        var domain = verify.Domains.Include(d => d.Reports).Single();
        Assert.Equal("contoso.io", domain.Name);
        Assert.True(domain.IsPinned);
        Assert.Single(domain.Reports);
    }

    [Fact]
    public async Task PollOnceAsync_AppendsNewlyDiscoveredDomain_AfterExistingCustomOrder()
    {
        using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, "existing-a.com", CancellationToken.None);
            await DomainManagementService.AddDomainAsync(context, "existing-b.com", CancellationToken.None);
            var existing = context.Domains.OrderBy(d => d.Name).ToList();
            await DomainManagementService.ReorderAsync(context, [existing[1].Id, existing[0].Id], CancellationToken.None);
        }

        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using var verify = CreateContext();
        var newDomain = verify.Domains.Single(d => d.Name == "contoso.io");
        Assert.Equal(2, newDomain.SortOrder);
    }
}
