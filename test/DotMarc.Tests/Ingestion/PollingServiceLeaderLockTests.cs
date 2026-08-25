using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

/// <summary>Covers PollingService.RunPollCycleAsync's Postgres advisory-lock leader election:
/// when multiple replicas run this service, only the one holding the lock for a given cycle
/// should actually poll the mailbox — others must skip that cycle rather than racing it.</summary>
[Collection("Postgres")]
public sealed class PollingServiceLeaderLockTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PollingServiceLeaderLockTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static FakeGraphMailboxClient GraphClientWithOneValidReport()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new DotMarc.Graph.MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new DotMarc.Graph.MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];
        return graphClient;
    }

    [Fact]
    public async Task RunPollCycleAsync_SkipsPolling_WhenAnotherInstanceHoldsTheLeaderLock()
    {
        var graphClient = GraphClientWithOneValidReport();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.PollingLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.RunPollCycleAsync(graphClient, context, CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            Assert.Empty(verify.Reports);
            Assert.Empty(verify.PollCycles);
        }
        Assert.DoesNotContain("msg-1", graphClient.MarkedAsRead);

        await lockTransaction.RollbackAsync();
    }

    [Fact]
    public async Task RunPollCycleAsync_ProcessesMessages_WhenTheLeaderLockIsFree()
    {
        var graphClient = GraphClientWithOneValidReport();

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.RunPollCycleAsync(graphClient, context, CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            Assert.Single(verify.Reports);
            var pollCycle = verify.PollCycles.Single();
            Assert.True(pollCycle.Succeeded);
            Assert.Equal(1, pollCycle.MessagesChecked);
            Assert.Equal(1, pollCycle.ReportsParsed);
            Assert.Equal(0, pollCycle.ParseFailures);
            Assert.Null(pollCycle.ErrorMessage);
        }
        Assert.Contains("msg-1", graphClient.MarkedAsRead);
    }

    [Fact]
    public async Task RunPollCycleAsync_CountsReportsParsedAndParseFailuresSeparately()
    {
        var graphClient = GraphClientWithOneValidReport();
        graphClient.UnreadMessages.Add(new DotMarc.Graph.MailboxMessage("msg-bad", "Not a report", true));
        graphClient.Attachments["msg-bad"] = [new DotMarc.Graph.MailboxAttachment("garbage.xml", "text/xml", "not xml"u8.ToArray())];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.RunPollCycleAsync(graphClient, context, CancellationToken.None);
        }

        using var verify = CreateContext();
        var pollCycle = verify.PollCycles.Single();
        Assert.Equal(2, pollCycle.MessagesChecked);
        Assert.Equal(1, pollCycle.ReportsParsed);
        Assert.Equal(1, pollCycle.ParseFailures);
        Assert.True(pollCycle.Succeeded);
    }

    [Fact]
    public async Task RunPollCycleAsync_RecordsAFailedCycle_WhenTheMailboxFetchThrows()
    {
        var graphClient = new FakeGraphMailboxClient { FailGetUnreadMessages = true };

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await Assert.ThrowsAsync<HttpRequestException>(() => service.RunPollCycleAsync(graphClient, context, CancellationToken.None));
        }

        using (var verify = CreateContext())
        {
            var pollCycle = verify.PollCycles.Single();
            Assert.False(pollCycle.Succeeded);
            Assert.Contains("Simulated Graph failure", pollCycle.ErrorMessage);
            Assert.Equal(0, pollCycle.MessagesChecked);
            Assert.Equal(0, pollCycle.ReportsParsed);
            Assert.Equal(0, pollCycle.ParseFailures);
        }
    }

    [Fact]
    public async Task RunPollCycleAsync_RollsUpStaleRows_AsPartOfRecordingTheCycle()
    {
        var graphClient = GraphClientWithOneValidReport();

        using (var seed = CreateContext())
        {
            seed.PollCycles.Add(new PollCycle
            {
                PolledUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-10),
                MessagesChecked = 1,
                ReportsParsed = 1,
                Succeeded = true
            });
            await seed.SaveChangesAsync();
        }

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.RunPollCycleAsync(graphClient, context, CancellationToken.None);
        }

        using var verify = CreateContext();
        Assert.Single(verify.PollCycles); // only the cycle this test just ran — the seeded stale row was rolled up and deleted
        Assert.Single(verify.PollCycleDailySummaries);
    }
}
