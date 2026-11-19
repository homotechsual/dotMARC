using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>An alert still open after a rule's escalation time is also sent to the rule's escalation destinations, once.</summary>
[Collection("Postgres")]
public sealed class EscalationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private FixedTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeAlertWebhookClient _client = new();
    private int _domainId;
    private int _slackId;

    public EscalationTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var slack = new AlertDestination { Name = "On-call Slack", Kind = AlertChannel.Slack, Url = "https://hooks.slack.com/services/T0/B0/x" };
        context.AlertDestinations.AddRange(new AlertDestination { Name = "Shared Teams", Kind = AlertChannel.Teams, Url = "https://example.test/shared" }, slack);
        var domain = new Domain { Name = "stale.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-90), LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-10) };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        (_domainId, _slackId) = (domain.Id, slack.Id);
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

    private Task EscalateAfterAsync(int minutes, bool withDestination = true) =>
        AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.MissedReport, rule =>
        {
            rule.EscalateAfterMinutes = minutes;
            if (withDestination)
            {
                rule.EscalationDestinations.Add(new AlertRuleEscalationDestination { AlertDestinationId = _slackId });
            }
        });

    private Task SetWindowAroundNowAsync(AlertQuietMode mode)
    {
        var minuteOfDay = (_clock.GetUtcNow().Hour * 60) + _clock.GetUtcNow().Minute;
        return AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.MissedReport, rule =>
            (rule.QuietMode, rule.QuietStartMinutes, rule.QuietEndMinutes) = (mode, (minuteOfDay - 60 + 1440) % 1440, (minuteOfDay + 60) % 1440));
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
    public async Task AnAlertStillOpenAfterTheEscalationTime_IsSentToTheEscalationDestinationsOnly_Once()
    {
        await EscalateAfterAsync(30);
        await RunCycleAsync();
        Assert.Single(_client.Destinations);

        _clock.Advance(TimeSpan.FromMinutes(29));
        await RunCycleAsync();
        Assert.Single(_client.Destinations);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await RunCycleAsync();
        Assert.Equal(2, _client.Destinations.Count);
        Assert.Equal(["On-call Slack"], _client.Destinations[1]);
        Assert.StartsWith("Escalated: ", _client.Titles[1]);
        Assert.Equal(_clock.GetUtcNow(), (await TheAlertAsync()).EscalatedUtc!.Value, TimeSpan.FromMilliseconds(1));

        _clock.Advance(TimeSpan.FromMinutes(60));
        await RunCycleAsync();
        Assert.Equal(2, _client.Destinations.Count);
    }

    [Fact]
    public async Task AnAlertResolvedBeforeTheEscalationTime_NeverEscalates()
    {
        await EscalateAfterAsync(30);
        await RunCycleAsync();

        await using (var context = CreateContext())
        {
            (await context.Domains.SingleAsync(domain => domain.Id == _domainId)).LastReportReceivedUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromMinutes(45));
        await RunCycleAsync();

        Assert.Single(_client.Destinations);
        Assert.Null((await TheAlertAsync()).EscalatedUtc);
    }

    [Fact]
    public async Task AHeldAlert_DoesNotEscalate()
    {
        await SetWindowAroundNowAsync(AlertQuietMode.Hold);
        await EscalateAfterAsync(30);
        await RunCycleAsync();

        _clock.Advance(TimeSpan.FromMinutes(40));
        await RunCycleAsync();

        Assert.Empty(_client.Destinations);
        Assert.Null((await TheAlertAsync()).EscalatedUtc);
    }

    [Fact]
    public async Task AMutedAlert_StillEscalates_BecauseEscalationIgnoresQuietHours()
    {
        await SetWindowAroundNowAsync(AlertQuietMode.Mute);
        await EscalateAfterAsync(30);
        await RunCycleAsync();
        Assert.Empty(_client.Destinations);

        _clock.Advance(TimeSpan.FromMinutes(40));
        await RunCycleAsync();

        Assert.Equal(["On-call Slack"], Assert.Single(_client.Destinations));
    }

    [Fact]
    public async Task ARuleWithAnEscalationTimeButNoEscalationDestinations_SendsNothingMore_AndDoesNotRetry()
    {
        await EscalateAfterAsync(30, withDestination: false);
        await RunCycleAsync();

        _clock.Advance(TimeSpan.FromMinutes(40));
        await RunCycleAsync();
        await RunCycleAsync();

        Assert.Single(_client.Destinations);
        Assert.NotNull((await TheAlertAsync()).EscalatedUtc);
    }

    [Fact]
    public async Task ARuleWithNoEscalationTime_NeverEscalates()
    {
        await RunCycleAsync();

        _clock.Advance(TimeSpan.FromMinutes(120));
        await RunCycleAsync();

        Assert.Single(_client.Destinations);
        Assert.Null((await TheAlertAsync()).EscalatedUtc);
    }
}
