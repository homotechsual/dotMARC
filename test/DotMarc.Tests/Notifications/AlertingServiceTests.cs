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
