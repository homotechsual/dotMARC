using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class DnsHealthAlertingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsHealthAlertingTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
            TeamsEnabled = true,
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

    private async Task EnableAlertsAsync()
    {
        await using var context = CreateContext();
        var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
        settings.Enabled = true;
        settings.TeamsWebhookUrl = "https://example.test/webhook";
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
    }

    private async Task<int> SeedDomainAsync(string name, Action<Domain>? configure = null)
    {
        await using var context = CreateContext();
        var checkedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var domain = new Domain
        {
            Name = name,
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30),
            LastReportReceivedUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = checkedUtc,
            SpfCheckStatus = SpfCheckStatus.Ok, SpfCheckedUtc = checkedUtc,
            MxCheckStatus = MxCheckStatus.Ok, MxCheckedUtc = checkedUtc,
            DmarcPolicy = DmarcPolicyLevel.Reject, DmarcSubdomainPolicy = DmarcPolicyLevel.Reject, DmarcPercent = 100,
            DnsNameservers = ["ns1.example.net", "ns2.example.net"], DnsProviderCheckedUtc = checkedUtc,
        };
        configure?.Invoke(domain);
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    private AlertingService CreateService(FakeAlertWebhookClient notifier) =>
        new(new FakeDbContextFactory(_connectionString), notifier, new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

    [Fact]
    public async Task FirstCycle_OnExistingDomains_RaisesNothing()
    {
        await EnableAlertsAsync();
        await SeedDomainAsync("healthy.example");
        await SeedDomainAsync("half-configured.example", domain =>
        {
            domain.SpfCheckStatus = SpfCheckStatus.MissingRecord;
            domain.TlsrptCheckStatus = TlsrptCheckStatus.MissingOwnRecord;
            domain.TlsrptCheckedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        });
        var notifier = new FakeAlertWebhookClient();

        await CreateService(notifier).CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.Empty(verify.AlertEvents);
        Assert.Equal(0, notifier.CallCount);
        Assert.Equal(18, await verify.DomainAlertStates.CountAsync());
    }

    [Fact]
    public async Task ABrokenCheck_RaisesOneAlertAfterItsRecheck_AndResolvesWhenItPasses()
    {
        await EnableAlertsAsync();
        var domainId = await SeedDomainAsync("contoso.example");
        var notifier = new FakeAlertWebhookClient();
        var service = CreateService(notifier);
        await service.CheckPinnedDomainsAsync();

        await using (var context = CreateContext())
        {
            var domain = await context.Domains.SingleAsync();
            domain.SpfCheckStatus = SpfCheckStatus.MissingRecord;
            domain.SpfCheckedUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await service.CheckPinnedDomainsAsync();
        await using (var context = CreateContext())
        {
            Assert.Empty(context.AlertEvents);
            var state = await context.DomainAlertStates.SingleAsync(candidate => candidate.DomainId == domainId && candidate.Item == DnsHealthItems.Spf);
            Assert.NotNull(state.RecheckDueUtc);

            // As if 15 minutes passed and polling rechecked SPF, which still fails.
            state.RecheckDueUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            (await context.Domains.SingleAsync()).SpfCheckedUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();
        await using (var context = CreateContext())
        {
            var alert = await context.AlertEvents.SingleAsync();
            Assert.Equal((AlertTypes.SpfRecordBroken, "contoso.example", "Warning", false), (alert.AlertType, alert.DomainName, alert.Severity, alert.IsResolved));
            Assert.Equal(1, notifier.CallCount);

            (await context.Domains.SingleAsync()).SpfCheckStatus = SpfCheckStatus.Ok;
            await context.SaveChangesAsync();
        }

        await service.CheckPinnedDomainsAsync();
        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task AlertsForADomainNoLongerMonitored_AreResolved()
    {
        await EnableAlertsAsync();
        await SeedDomainAsync("old.example", domain => domain.IsMonitored = false);
        await using (var context = CreateContext())
        {
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "old.example", AlertType = AlertTypes.SpfRecordBroken, Severity = "Warning", Title = "SPF record broken", Message = "m"
            });
            await context.SaveChangesAsync();
        }

        await CreateService(new FakeAlertWebhookClient()).CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task NothingIsEvaluated_WhenAlertsAreDisabled()
    {
        await SeedDomainAsync("contoso.example");
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.Enabled = false;
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
        }

        await CreateService(new FakeAlertWebhookClient()).CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.Empty(verify.DomainAlertStates);
    }

    [Fact]
    public async Task AFixedCheck_ClosesEveryOpenCopyOfItsAlert_InOneCycle()
    {
        // A long outage re-raises the alert each cooldown, leaving several open copies, each with its own ticket.
        await EnableAlertsAsync();
        await SeedDomainAsync("contoso.example");
        await using (var context = CreateContext())
        {
            for (var copy = 0; copy < 3; copy++)
            {
                context.AlertEvents.Add(new AlertEvent
                {
                    DomainName = "contoso.example", AlertType = AlertTypes.SpfRecordBroken, Severity = "Warning", Title = "SPF record broken",
                    Message = "m", CreatedUtc = DateTimeOffset.UtcNow.AddHours(-copy * 3),
                });
            }

            await context.SaveChangesAsync();
        }

        await CreateService(new FakeAlertWebhookClient()).CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.All(await verify.AlertEvents.ToListAsync(), alert => Assert.True(alert.IsResolved));
    }
}
