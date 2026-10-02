using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertAcknowledgementTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertAcknowledgementTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task SeedSettingsAsync(bool enabled = true, int missingReportThresholdDays = 2, int cooldownMinutes = 180, int suspiciousRejectMinVolume = 10, int suspiciousRejectNonBenignPercent = 50)
    {
        await using var context = CreateContext();
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings
        {
            Enabled = enabled,
            DeliveryMode = "Teams",
            TeamsWebhookUrl = "https://example.test/webhook",
            MissingReportThresholdDays = missingReportThresholdDays,
            CooldownMinutes = cooldownMinutes,
            SuspiciousRejectMinVolume = suspiciousRejectMinVolume,
            SuspiciousRejectNonBenignPercent = suspiciousRejectNonBenignPercent
        });
    }

    private async Task SeedMonitoredDomainAsync(string name, DateTimeOffset? lastReportReceivedUtc)
    {
        await using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = name,
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
            LastReportReceivedUtc = lastReportReceivedUtc
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedNullRoutedDomainAsync(string name)
    {
        await using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = name,
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
            LastReportReceivedUtc = null,
            SpfCheckStatus = SpfCheckStatus.NullSpf
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedMonitoredDomainWithRejectsAsync(string name, DateTimeOffset lastReportReceivedUtc, params (int MessageCount, DmarcPolicyOverrideType? ReasonType)[] rejectedRecords)
    {
        await using var context = CreateContext();
        var domain = new Domain
        {
            Name = name,
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
            LastReportReceivedUtc = lastReportReceivedUtc
        };
        var report = new Report
        {
            ReportingOrg = "google.com",
            ReportId = Guid.NewGuid().ToString(),
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = lastReportReceivedUtc,
            AuthDetailBackfilledUtc = DateTimeOffset.UtcNow
        };
        foreach (var (messageCount, reasonType) in rejectedRecords)
        {
            var record = new ReportRecord { SourceIp = "203.0.113.9", MessageCount = messageCount, Disposition = DispositionResult.Reject, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail, HeaderFrom = name };
            if (reasonType is { } type)
            {
                record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = type });
            }
            report.Records.Add(record);
        }
        domain.Reports.Add(report);
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
    }

    private async Task<int> SeedPolicyAlertAsync(string ticketId = "777", bool secondOpenCopy = false)
    {
        await using var context = CreateContext();
        var domain = new Domain
        {
            Name = "contoso.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30), LastReportReceivedUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            DmarcPolicy = DmarcPolicyLevel.Quarantine, DmarcSubdomainPolicy = DmarcPolicyLevel.Quarantine, DmarcPercent = 100,
        };
        domain.AlertStates.Add(new DomainAlertState
        {
            Item = DnsHealthItems.DmarcPolicy, Baseline = "p=reject; sp=reject; pct=100",
            PendingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2), RecheckDueUtc = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(15),
        });
        context.Domains.Add(domain);
        var alert = NewPolicyAlert(ticketId);
        context.AlertEvents.Add(alert);
        if (secondOpenCopy)
        {
            context.AlertEvents.Add(NewPolicyAlert("778"));
        }

        await context.SaveChangesAsync();
        return alert.Id;
    }

    private static AlertEvent NewPolicyAlert(string ticketId) => new()
    {
        DomainName = "contoso.example", AlertType = AlertTypes.DmarcPolicyWeakened, Severity = "Warning", Title = "DMARC policy weakened",
        Message = "m", ExternalTicketProvider = "HaloPSA", ExternalTicketId = ticketId,
    };

    [Fact]
    public async Task Acknowledge_ClosesEveryOpenCopy_AcceptsThePolicy_ClosesTheTickets_AndIsAudited()
    {
        var alertId = await SeedPolicyAlertAsync(secondOpenCopy: true);
        var halo = new ClosingHaloPsaClient();

        AcknowledgeOutcome outcome;
        await using (var context = CreateContext())
        {
            outcome = await AlertAcknowledgement.AcknowledgeAsync(context, TestActors.Admin, alertId, new PsaTicketService(halo));
        }

        Assert.Equal(AcknowledgeOutcome.Acknowledged, outcome);
        Assert.Equal(["777", "778"], halo.ClosedTicketIds.Order());
        await using var verify = CreateContext();
        Assert.All(await verify.AlertEvents.ToListAsync(), alert => Assert.True(alert.IsResolved));
        var state = await verify.DomainAlertStates.SingleAsync();
        Assert.Equal("p=quarantine; sp=quarantine; pct=100", state.Baseline);
        Assert.Null(state.PendingSinceUtc);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.AlertAcknowledged, entry.Action);
        Assert.Contains("contoso.example", entry.Summary);
    }

    [Fact]
    public async Task Acknowledge_AcceptsTheCurrentPolicy_SoTheNextCycleRaisesNothing()
    {
        var alertId = await SeedPolicyAlertAsync();
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.TeamsWebhookUrl = "https://example.test/webhook";
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
            await AlertAcknowledgement.AcknowledgeAsync(context, TestActors.Admin, alertId, new PsaTicketService(new ClosingHaloPsaClient()));
        }

        var notifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), notifier, new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.Single(verify.AlertEvents);
        Assert.Equal(0, notifier.CallCount);
    }

    [Fact]
    public async Task Acknowledge_StillClosesTheAlert_WhenTheTicketCantBeClosed()
    {
        var alertId = await SeedPolicyAlertAsync();

        AcknowledgeOutcome outcome;
        await using (var context = CreateContext())
        {
            outcome = await AlertAcknowledgement.AcknowledgeAsync(context, TestActors.Admin, alertId, new PsaTicketService(new ClosingHaloPsaClient { Fails = true }));
        }

        Assert.Equal(AcknowledgeOutcome.AcknowledgedButTicketNotClosed, outcome);
        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task ACheckAlert_CantBeAcknowledged()
    {
        int alertId;
        await using (var context = CreateContext())
        {
            var alert = new AlertEvent { DomainName = "contoso.example", AlertType = AlertTypes.SpfRecordBroken, Severity = "Warning", Title = "SPF record broken", Message = "m" };
            context.AlertEvents.Add(alert);
            await context.SaveChangesAsync();
            alertId = alert.Id;
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(AcknowledgeOutcome.NotAcknowledgeable,
                await AlertAcknowledgement.AcknowledgeAsync(context, TestActors.Admin, alertId, new PsaTicketService(new ClosingHaloPsaClient())));
        }

        await using var verify = CreateContext();
        Assert.False((await verify.AlertEvents.SingleAsync()).IsResolved);
        Assert.Empty(verify.AuditEntries);
    }

    [Fact]
    public async Task AnOldPolicyAlert_IsClosedAutomatically_WhenAutoCloseIsOn()
    {
        var alertId = await SeedPolicyAlertAsync();
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.TeamsWebhookUrl = "https://example.test/webhook";
            settings.AcknowledgeableAutoCloseDays = 3;
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
            (await context.AlertEvents.SingleAsync(alert => alert.Id == alertId)).CreatedUtc = DateTimeOffset.UtcNow.AddDays(-4);
            await context.SaveChangesAsync();
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync()).IsResolved);
        Assert.Equal("p=quarantine; sp=quarantine; pct=100", (await verify.DomainAlertStates.SingleAsync(state => state.Item == DnsHealthItems.DmarcPolicy)).Baseline);
        var entry = await verify.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.AlertAcknowledged);
        Assert.Equal(AuditActorKind.System, entry.ActorKind);
    }

    [Fact]
    public async Task ARecentPolicyAlert_IsLeftOpen_WhenAutoCloseIsOn()
    {
        await SeedPolicyAlertAsync();
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.TeamsWebhookUrl = "https://example.test/webhook";
            settings.AcknowledgeableAutoCloseDays = 3;
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.False((await verify.AlertEvents.SingleAsync()).IsResolved);
    }

    private sealed class ClosingHaloPsaClient : IHaloPsaClient
    {
        public bool Fails { get; init; }
        public List<string> ClosedTicketIds { get; } = [];

        public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
        {
            if (Fails)
            {
                throw new HttpRequestException("Halo is down.");
            }

            ClosedTicketIds.Add(ticketId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloClient>>([]);
        public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketType>>([]);
        public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketStatus>>([]);
        public Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloPriority>>([]);
        public Task<IReadOnlyList<HaloAgent>> ListAgentsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloAgent>>([]);
        public Task<int> GetTicketStatusAsync(HaloPsaSettings settings, int ticketId, CancellationToken cancellationToken = default) => Task.FromResult(9);
        public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default) => Task.FromResult("unused");
    }
}
