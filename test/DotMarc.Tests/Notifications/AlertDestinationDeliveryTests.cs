using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>An alert goes to the destinations its rule chooses, through the real alerting cycle.</summary>
[Collection("Postgres")]
public sealed class AlertDestinationDeliveryTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private int _secondId;

    public AlertDestinationDeliveryTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var first = new AlertDestination { Name = "Shared Teams", Kind = AlertChannel.Teams, Url = "https://example.test/shared" };
        var second = new AlertDestination { Name = "Aurora Slack", Kind = AlertChannel.Slack, Url = "https://hooks.slack.com/services/T0/B0/x" };
        context.AlertDestinations.AddRange(first, second);
        context.Domains.Add(new Domain { Name = "stale.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-90), LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-10) });
        await context.SaveChangesAsync();
        _secondId = second.Id;
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

    private async Task<FakeAlertWebhookClient> RunCycleAsync()
    {
        var client = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), client, PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();
        return client;
    }

    [Fact]
    public async Task ARuleInAllMode_SendsToEveryDestination()
    {
        var client = await RunCycleAsync();

        Assert.Equal(["Aurora Slack", "Shared Teams"], client.Destinations.Single().Order());
    }

    [Fact]
    public async Task ASelectedRule_SendsOnlyToItsDestination()
    {
        await AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.MissedReport, rule =>
        {
            rule.DestinationMode = AlertDestinationMode.Selected;
            rule.Destinations.Add(new AlertRuleDestination { AlertDestinationId = _secondId });
        });

        var client = await RunCycleAsync();

        Assert.Equal(["Aurora Slack"], client.Destinations.Single());
    }

    [Fact]
    public async Task ASelectedRuleWithNoDestinations_StillRaisesTheAlert_ButSendsNowhere()
    {
        await AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.MissedReport, rule => rule.DestinationMode = AlertDestinationMode.Selected);

        var client = await RunCycleAsync();

        Assert.Empty(client.Destinations.Single());
        await using var context = CreateContext();
        Assert.Single(await context.AlertEvents.Where(alert => alert.AlertType == AlertTypes.MissedReport).ToListAsync());
    }

    [Fact]
    public async Task WhenNotificationsAreSwitchedOff_NothingIsSent_ButTheAlertIsStillRaised()
    {
        await using (var context = CreateContext())
        {
            (await NotificationSettingsService.GetAsync(context)).Enabled = false;
            await context.SaveChangesAsync();
        }

        var client = await RunCycleAsync();

        Assert.All(client.Destinations, names => Assert.Empty(names));
    }
}
