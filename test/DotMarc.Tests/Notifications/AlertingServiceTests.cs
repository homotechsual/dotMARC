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

    private async Task SeedSettingsAsync(bool enabled = true, int missingReportThresholdDays = 2, int cooldownMinutes = 180)
    {
        await using var context = CreateContext();
        await NotificationSettingsService.SaveAsync(context, new NotificationSettings
        {
            Enabled = enabled,
            DeliveryMode = "Teams",
            TeamsWebhookUrl = "https://example.test/webhook",
            MissingReportThresholdDays = missingReportThresholdDays,
            CooldownMinutes = cooldownMinutes
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

    [Fact]
    public async Task CheckPinnedDomainsAsync_CreatesOneMissedReportAlert_PerDomainWithinCooldown()
    {
        await SeedSettingsAsync();
        await SeedMonitoredDomainAsync("contoso.io", DateTimeOffset.UtcNow.AddDays(-3));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, NullLogger<AlertingService>.Instance);

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
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, NullLogger<AlertingService>.Instance);

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
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        var alert = await verifyContext.AlertEvents.SingleAsync();
        Assert.Equal("The monitored domain 'contoso.io' has not received a DMARC report yet.", alert.Message);
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
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), notifier, NullLogger<AlertingService>.Instance);

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
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), NullLogger<AlertingService>.Instance);
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
}
