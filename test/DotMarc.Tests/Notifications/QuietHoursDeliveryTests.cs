using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>An alert raised in a rule's quiet window is held or muted, through the real alerting cycle with a clock the test moves.</summary>
[Collection("Postgres")]
public sealed class QuietHoursDeliveryTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private FixedTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeAlertWebhookClient _client = new();
    private int _domainId;

    public QuietHoursDeliveryTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        context.AlertDestinations.Add(new AlertDestination { Name = "Shared Teams", Kind = AlertChannel.Teams, Url = "https://example.test/shared" });
        var domain = new Domain { Name = "stale.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-90), LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-10) };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        _domainId = domain.Id;
        _clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
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

    /// <summary>A two hour window centred on the clock's now, in UTC, so a test does not depend on the time of day it runs.</summary>
    private Task SetWindowAroundNowAsync(AlertQuietMode mode, bool ignoredForCritical = true, string severity = "Warning")
    {
        var minuteOfDay = (_clock.GetUtcNow().Hour * 60) + _clock.GetUtcNow().Minute;
        var start = (minuteOfDay - 60 + 1440) % 1440;
        var end = (minuteOfDay + 60) % 1440;
        return AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.MissedReport, rule =>
        {
            (rule.QuietMode, rule.QuietStartMinutes, rule.QuietEndMinutes, rule.QuietIgnoredForCritical, rule.Severity) = (mode, start, end, ignoredForCritical, severity);
        });
    }

    private async Task RunCycleAsync()
    {
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), _client, PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance, _clock);
        await service.CheckPinnedDomainsAsync();
    }

    private async Task<AlertEvent> TheAlertAsync()
    {
        await using var context = CreateContext();
        return await context.AlertEvents.AsNoTracking().SingleAsync(alert => alert.AlertType == AlertTypes.MissedReport);
    }

    [Fact]
    public async Task OutsideAnyWindow_TheAlertIsSentAtOnce_AndMarkedNotified()
    {
        await RunCycleAsync();

        Assert.Single(_client.Destinations);
        Assert.NotNull((await TheAlertAsync()).NotifiedUtc);
    }

    [Fact]
    public async Task Hold_RecordsTheAlertButSendsNothing_ThenSendsOnceAfterTheWindow()
    {
        await SetWindowAroundNowAsync(AlertQuietMode.Hold);

        await RunCycleAsync();
        Assert.Empty(_client.Destinations);
        Assert.Null((await TheAlertAsync()).NotifiedUtc);

        // Still inside the window half an hour on: still held.
        _clock.Advance(TimeSpan.FromMinutes(30));
        await RunCycleAsync();
        Assert.Empty(_client.Destinations);

        // Two hours on the window has ended: sent once, and only once.
        _clock.Advance(TimeSpan.FromMinutes(90));
        await RunCycleAsync();
        Assert.Single(_client.Destinations);
        Assert.Equal(_clock.GetUtcNow(), (await TheAlertAsync()).NotifiedUtc!.Value, TimeSpan.FromMilliseconds(1));
        await RunCycleAsync();
        Assert.Single(_client.Destinations);
    }

    [Fact]
    public async Task Hold_NeverSendsAnAlertThatWasResolvedBeforeTheWindowEnded()
    {
        await SetWindowAroundNowAsync(AlertQuietMode.Hold);
        await RunCycleAsync();

        await using (var context = CreateContext())
        {
            (await context.Domains.SingleAsync(domain => domain.Id == _domainId)).LastReportReceivedUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromMinutes(120));
        await RunCycleAsync();

        Assert.Empty(_client.Destinations);
        Assert.True((await TheAlertAsync()).IsResolved);
    }

    [Fact]
    public async Task Mute_RecordsTheAlertAsNotified_AndSendsNothingNowOrAfterTheWindow()
    {
        await SetWindowAroundNowAsync(AlertQuietMode.Mute);

        await RunCycleAsync();
        Assert.Empty(_client.Destinations);
        Assert.NotNull((await TheAlertAsync()).NotifiedUtc);

        _clock.Advance(TimeSpan.FromMinutes(120));
        await RunCycleAsync();
        Assert.Empty(_client.Destinations);
    }

    [Fact]
    public async Task ACriticalRule_IsSentInsideTheWindow_WhenItIgnoresQuietHoursForCritical()
    {
        await SetWindowAroundNowAsync(AlertQuietMode.Hold, ignoredForCritical: true, severity: AlertSeverities.Critical);

        await RunCycleAsync();

        Assert.Single(_client.Destinations);
    }

    [Fact]
    public async Task ACriticalRule_IsHeldInsideTheWindow_WhenItDoesNotIgnoreQuietHours()
    {
        await SetWindowAroundNowAsync(AlertQuietMode.Hold, ignoredForCritical: false, severity: AlertSeverities.Critical);

        await RunCycleAsync();

        Assert.Empty(_client.Destinations);
    }

    [Fact]
    public async Task AnUnknownTimeZoneSetting_ActsAsUtc()
    {
        await using (var context = CreateContext())
        {
            (await NotificationSettingsService.GetAsync(context)).TimeZoneId = "Not/AZone";
            await context.SaveChangesAsync();
        }

        await SetWindowAroundNowAsync(AlertQuietMode.Hold);
        await RunCycleAsync();

        Assert.Empty(_client.Destinations);
    }
}
