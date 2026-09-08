using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertingServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertingServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        await NotificationSettingsService.SaveAsync(context, new NotificationSettings
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

    [Fact]
    public async Task CheckPinnedDomainsAsync_CreatesOneMissedReportAlert_PerDomainWithinCooldown()
    {
        await SeedSettingsAsync();
        await SeedMonitoredDomainAsync("contoso.io", DateTimeOffset.UtcNow.AddDays(-3));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        var alerts = await verifyContext.AlertEvents.OrderBy(e => e.CreatedUtc).ToListAsync();

        Assert.Single(alerts);
        Assert.Equal("MissedReport", alerts[0].AlertType);
        Assert.Equal("contoso.io", alerts[0].DomainName);
        Assert.Equal(1, fakeNotifier.CallCount);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_DoesNothing_WhenSettingsAreDisabled()
    {
        await SeedSettingsAsync(enabled: false);
        await SeedMonitoredDomainAsync("contoso.io", DateTimeOffset.UtcNow.AddDays(-30));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        Assert.Empty(verifyContext.AlertEvents);
        Assert.Equal(0, fakeNotifier.CallCount);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_ExplainsWhenAMonitoredDomainHasNeverReceivedAReport()
    {
        await SeedSettingsAsync();
        await SeedMonitoredDomainAsync("contoso.io", lastReportReceivedUtc: null);

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        var alert = await verifyContext.AlertEvents.SingleAsync();
        Assert.Equal("The monitored domain 'contoso.io' has not received a DMARC report yet.", alert.Message);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_SkipsMissingReportCheck_ForANullRoutedDomain()
    {
        await SeedSettingsAsync();
        await SeedNullRoutedDomainAsync("contoso.io");

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        Assert.Empty(verifyContext.AlertEvents);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_ResolvesStaleMissedReportAlert_ForADomainThatBecameNullRouted()
    {
        await SeedSettingsAsync();
        await using (var context = CreateContext())
        {
            context.Domains.Add(new Domain
            {
                Name = "contoso.io",
                IsMonitored = true,
                FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
                LastReportReceivedUtc = null,
                SpfCheckStatus = SpfCheckStatus.NullSpf
            });
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "contoso.io",
                AlertType = "MissedReport",
                Severity = "Warning",
                Title = "Missing expected DMARC report",
                Message = "stale, from before this domain became null-routed",
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-5)
            });
            await context.SaveChangesAsync();
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync();
        Assert.True(alert.IsResolved);
        Assert.NotNull(alert.ResolvedUtc);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_ResolvesUnexpectedActivityAlert_ForANullRoutedDomainThatHasGoneQuietAgain()
    {
        await SeedSettingsAsync();
        await using (var context = CreateContext())
        {
            context.Domains.Add(new Domain
            {
                Name = "contoso.io",
                IsMonitored = true,
                FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
                LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-5),
                SpfCheckStatus = SpfCheckStatus.NullSpf
            });
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "contoso.io",
                AlertType = "UnexpectedActivityOnNullRoutedDomain",
                Severity = "Warning",
                Title = "Unexpected mail activity on a null-routed domain",
                Message = "unexpected activity detected earlier",
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-3)
            });
            await context.SaveChangesAsync();
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync(e => e.AlertType == "UnexpectedActivityOnNullRoutedDomain");
        Assert.True(alert.IsResolved);
        Assert.NotNull(alert.ResolvedUtc);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_DoesNotResolveUnexpectedActivityAlert_ForANullRoutedDomainWithARecentReport()
    {
        await SeedSettingsAsync();
        await using (var context = CreateContext())
        {
            context.Domains.Add(new Domain
            {
                Name = "contoso.io",
                IsMonitored = true,
                FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
                LastReportReceivedUtc = DateTimeOffset.UtcNow.AddHours(-1),
                SpfCheckStatus = SpfCheckStatus.NullSpf
            });
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "contoso.io",
                AlertType = "UnexpectedActivityOnNullRoutedDomain",
                Severity = "Warning",
                Title = "Unexpected mail activity on a null-routed domain",
                Message = "unexpected activity detected just now",
                CreatedUtc = DateTimeOffset.UtcNow.AddHours(-1)
            });
            await context.SaveChangesAsync();
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync(e => e.AlertType == "UnexpectedActivityOnNullRoutedDomain");
        Assert.False(alert.IsResolved);
        Assert.Null(alert.ResolvedUtc);
    }

    [Fact]
    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync_CreatesAnAlert()
    {
        await SeedSettingsAsync();
        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.FlagUnexpectedActivityForNullRoutedDomainAsync("contoso.io", CancellationToken.None);

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync();
        Assert.Equal("UnexpectedActivityOnNullRoutedDomain", alert.AlertType);
        Assert.Equal("contoso.io", alert.DomainName);
        Assert.Contains("null-routed", alert.Message);
        Assert.Equal(1, fakeNotifier.CallCount);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_CreatesSuspiciousRejectActivityAlert_WhenRejectsAreMostlyNonBenign()
    {
        await SeedSettingsAsync(suspiciousRejectMinVolume: 10, suspiciousRejectNonBenignPercent: 50);
        await SeedMonitoredDomainWithRejectsAsync("contoso.io", DateTimeOffset.UtcNow, (20, null));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        var alert = await verifyContext.AlertEvents.SingleAsync(e => e.AlertType == "SuspiciousRejectActivity");
        Assert.Equal("contoso.io", alert.DomainName);
        Assert.False(alert.IsResolved);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_DoesNotCreateSuspiciousRejectActivityAlert_WhenRejectsAreMostlyBenign()
    {
        await SeedSettingsAsync(suspiciousRejectMinVolume: 10, suspiciousRejectNonBenignPercent: 50);
        await SeedMonitoredDomainWithRejectsAsync("contoso.io", DateTimeOffset.UtcNow, (20, DmarcPolicyOverrideType.TrustedForwarder));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        Assert.False(await verifyContext.AlertEvents.AnyAsync(e => e.AlertType == "SuspiciousRejectActivity"));
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_DoesNotCreateSuspiciousRejectActivityAlert_BelowMinVolume()
    {
        await SeedSettingsAsync(suspiciousRejectMinVolume: 100, suspiciousRejectNonBenignPercent: 50);
        await SeedMonitoredDomainWithRejectsAsync("contoso.io", DateTimeOffset.UtcNow, (20, null));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        Assert.False(await verifyContext.AlertEvents.AnyAsync(e => e.AlertType == "SuspiciousRejectActivity"));
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_ResolvesSuspiciousRejectActivityAlert_OnceRatioDropsBelowThreshold()
    {
        await SeedSettingsAsync(suspiciousRejectMinVolume: 10, suspiciousRejectNonBenignPercent: 50, cooldownMinutes: 0);
        await SeedMonitoredDomainWithRejectsAsync("contoso.io", DateTimeOffset.UtcNow, (20, null));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();

        await using (var midContext = CreateContext())
        {
            Assert.True(await midContext.AlertEvents.AnyAsync(e => e.AlertType == "SuspiciousRejectActivity" && !e.IsResolved));
        }

        // The underlying activity now looks benign - mutate the existing record's reasons in
        // place rather than reseeding, so this is still the same domain/report the first check saw.
        await using (var mutate = CreateContext())
        {
            var record = await mutate.ReportRecords.Include(r => r.OverrideReasons).SingleAsync(r => r.SourceIp == "203.0.113.9");
            record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = DmarcPolicyOverrideType.MailingList });
            await mutate.SaveChangesAsync();
        }

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        var alert = await verifyContext.AlertEvents.Where(e => e.AlertType == "SuspiciousRejectActivity").OrderByDescending(e => e.CreatedUtc).FirstAsync();
        Assert.True(alert.IsResolved);
    }

    [Fact]
    public async Task MonitorAdvisoryLock_IsGrantedToOnlyOneDatabaseSession()
    {
        await using var first = CreateContext();
        await using var second = CreateContext();
        await first.Database.OpenConnectionAsync();
        await second.Database.OpenConnectionAsync();

        Assert.True(await TryAcquireAdvisoryLockAsync(first));
        Assert.False(await TryAcquireAdvisoryLockAsync(second));
    }

    [Fact]
    public async Task HandleTlsrptReportAsync_CreatesOneAlertForReportedTlsDeliveryFailures()
    {
        await SeedSettingsAsync();
        var notifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), notifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.HandleTlsrptReportAsync("contoso.io", 3, ["certificate-expired"], CancellationToken.None);
        await service.HandleTlsrptReportAsync("contoso.io", 3, ["certificate-expired"], CancellationToken.None);

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync();
        Assert.Equal("TlsrptFailure", alert.AlertType);
        Assert.Contains("3 failed TLS delivery session(s)", alert.Message);
        Assert.Equal(1, notifier.CallCount);
    }

    [Fact]
    public async Task HandleTlsrptReportAsync_ResolvesFailureAlertWhenALaterReportHasNoFailures()
    {
        await SeedSettingsAsync();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);
        await service.HandleTlsrptReportAsync("contoso.io", 1, ["certificate-expired"], CancellationToken.None);

        await service.HandleTlsrptReportAsync("contoso.io", 0, [], CancellationToken.None);

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync();
        Assert.True(alert.IsResolved);
        Assert.NotNull(alert.ResolvedUtc);
    }

    private static async Task<bool> TryAcquireAdvisoryLockAsync(DotMarcDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(829384120733591644)";
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FakeAlertWebhookClient : IAlertWebhookClient
    {
        public int CallCount { get; private set; }

        public Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private static IPsaTicketService CreateNoOpPsaTicketService() => new PsaTicketService(new NoOpHaloPsaClient());

    private sealed class NoOpHaloPsaClient : IHaloPsaClient
    {
        public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloClient>>([]);
        public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketType>>([]);
        public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketStatus>>([]);
        public Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloPriority>>([]);
        public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default) => Task.FromResult("unused");
        public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
