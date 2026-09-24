using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

public class HaloWebhookActivityTests
{
    private static HaloWebhookActivity CreateActivity() => new(NullLogger<HaloWebhookActivity>.Instance);

    [Fact]
    public void Recent_ReturnsTheNewestFirst_AndKeepsOnlyTheLatestHundred()
    {
        var activity = CreateActivity();
        for (var ticket = 1; ticket <= 150; ticket++)
        {
            activity.Record(HaloWebhookDelivery.ClosedStatus, ticket, 9);
        }

        var recent = activity.Recent(500);

        Assert.Equal(100, recent.Count);
        Assert.Equal(150, recent[0].TicketId);
        Assert.Equal(51, recent[^1].TicketId);
    }

    [Fact]
    public async Task WaitFor_ReturnsAReceiptThatArrivesWhileWaiting()
    {
        var activity = CreateActivity();
        var since = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            activity.Record(HaloWebhookDelivery.ClosedStatus, 42, 9);
        });

        var receipt = await activity.WaitForAsync(r => r.TicketId == 42, since, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(receipt);
        Assert.Equal(HaloWebhookDelivery.ClosedStatus, receipt.Delivery);
    }

    [Fact]
    public async Task WaitFor_ReturnsNull_WhenNothingMatchesBeforeTheTimeout()
    {
        var activity = CreateActivity();
        activity.Record(HaloWebhookDelivery.ClosedStatus, 7, 9);

        var receipt = await activity.WaitForAsync(r => r.TicketId == 42, DateTimeOffset.MinValue, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(receipt);
    }

    [Fact]
    public async Task WaitFor_IgnoresReceiptsFromBeforeTheStartTime()
    {
        var activity = CreateActivity();
        activity.Record(HaloWebhookDelivery.ClosedStatus, 42, 9);
        await Task.Delay(30);

        var receipt = await activity.WaitForAsync(r => r.TicketId == 42, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(receipt);
    }
}

[Collection("Postgres")]
public sealed class HaloIntegrationTestServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(400);

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private readonly HaloWebhookActivity _activity = new(NullLogger<HaloWebhookActivity>.Instance);
    private readonly FakeHalo _halo = new();

    public HaloIntegrationTestServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private HaloIntegrationTestService CreateService() =>
        new(new FakeDbContextFactory(_connectionString), _halo, _activity, NullLogger<HaloIntegrationTestService>.Instance);

    private sealed class FakeHalo : IHaloPsaClient
    {
        public List<string> Calls { get; } = [];
        public int? LastClientId { get; private set; }
        public string? LastTitle { get; private set; }
        public string TicketId { get; set; } = "4242";
        public Exception? CreateFailure { get; set; }
        public Exception? CloseFailure { get; set; }

        /// <summary>Stands in for Halo: runs after the ticket is closed, the way Halo's own webhook
        /// call would arrive shortly afterwards.</summary>
        public Action? AfterClose { get; set; }

        public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloClient>>([]);
        public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketType>>([]);
        public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketStatus>>([]);
        public Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloPriority>>([]);
        public Task<IReadOnlyList<HaloAgent>> ListAgentsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloAgent>>([]);

        public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
        {
            Calls.Add("create");
            LastClientId = haloClientId;
            LastTitle = title;
            return CreateFailure is null ? Task.FromResult(TicketId) : Task.FromException<string>(CreateFailure);
        }

        public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
        {
            Calls.Add("close");
            if (CloseFailure is not null)
            {
                return Task.FromException(CloseFailure);
            }

            AfterClose?.Invoke();
            return Task.CompletedTask;
        }
    }

    private async Task SeedSettingsAsync(bool enabled = true, bool complete = true)
    {
        await using var context = CreateContext();
        var settings = await context.HaloPsaSettings.SingleAsync();
        settings.Enabled = enabled;
        if (complete)
        {
            settings.AccountName = "contoso";
            settings.AuthServerUrl = "https://contoso.halopsa.com/auth";
            settings.ResourceServerUrl = "https://contoso.halopsa.com/api";
            settings.ClientId = "client-id";
            settings.ClientSecretConfigured = true;
            settings.TicketTypeId = 5;
            settings.DefaultPriorityId = 2;
            settings.ClosedStatusId = 9;
            settings.WebhookSecret = "the-secret";
        }

        await context.SaveChangesAsync();
    }

    private async Task SeedAlertAsync(int? mappedHaloClientId)
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        if (mappedHaloClientId is { } haloClientId)
        {
            domain.Groups.Add(new Group { Name = "Contoso", HaloClientId = haloClientId });
        }

        context.Domains.Add(domain);
        context.AlertEvents.Add(new AlertEvent
        {
            DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning",
            Title = "Missing expected DMARC report", Message = "contoso.io has not sent a report."
        });
        await context.SaveChangesAsync();
    }

    private void HaloCallsTheWebhookAfterClosing(HaloWebhookDelivery delivery = HaloWebhookDelivery.ClosedStatus, int statusId = 9) =>
        _halo.AfterClose = () => _activity.Record(delivery, 4242, statusId);

    [Fact]
    public async Task Run_CreatesClosesAndSeesTheWebhook_WhenEverythingIsConfigured()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        HaloCallsTheWebhookAfterClosing();

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.Equal(HaloIntegrationTestService.StepNames, run.Steps.Select(s => s.Name));
        Assert.All(run.Steps, step => Assert.Equal(HaloTestOutcome.Passed, step.Outcome));
        Assert.Equal(["create", "close"], _halo.Calls);
        Assert.Equal(7, _halo.LastClientId);
        Assert.StartsWith("[dotMARC test] ", _halo.LastTitle);
        Assert.Contains("Missing expected DMARC report", _halo.LastTitle);
    }

    [Fact]
    public async Task Run_StopsAtTheSettings_WithoutCallingHalo_WhenTheyAreIncomplete()
    {
        await SeedSettingsAsync(complete: false);

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.False(run.Succeeded);
        var step = Assert.Single(run.Steps);
        Assert.Equal(HaloTestOutcome.Failed, step.Outcome);
        Assert.Contains("closed status", step.Detail);
        Assert.Contains("client secret", step.Detail);
        Assert.Empty(_halo.Calls);
    }

    [Fact]
    public async Task Run_WarnsButCarriesOn_WhenTicketSyncIsSwitchedOff()
    {
        await SeedSettingsAsync(enabled: false);
        await SeedAlertAsync(mappedHaloClientId: 7);
        HaloCallsTheWebhookAfterClosing();

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.Equal(HaloTestOutcome.Warning, run.Steps[0].Outcome);
        Assert.Contains("switched off", run.Steps[0].Detail);
        Assert.True(run.Succeeded);
    }

    [Fact]
    public async Task Run_AsksForAClient_WhenTheLatestAlertsDomainIsNotMapped()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: null);

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.True(run.NeedsClientChoice);
        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(HaloIntegrationTestService.AlertStep, step.Name);
        Assert.Equal(HaloTestOutcome.Failed, step.Outcome);
        Assert.Contains("contoso.io", step.Detail);
        Assert.Empty(_halo.Calls);
    }

    [Fact]
    public async Task Run_UsesTheChosenClient_WhenTheDomainIsNotMapped()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: null);
        HaloCallsTheWebhookAfterClosing();

        var run = await CreateService().RunAsync(12, null, ShortTimeout, CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.Equal(12, _halo.LastClientId);
        var alertStep = run.Steps.Single(s => s.Name == HaloIntegrationTestService.AlertStep);
        Assert.Equal(HaloTestOutcome.Warning, alertStep.Outcome);
        Assert.Contains("no Halo client mapped", alertStep.Detail);
    }

    [Fact]
    public async Task Run_AsksForAClient_WhenThereAreNoAlertsYet_AndUsesASampleOnceOneIsChosen()
    {
        await SeedSettingsAsync();

        var withoutClient = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);
        Assert.True(withoutClient.NeedsClientChoice);
        Assert.Contains("no alerts yet", withoutClient.Steps.Last().Detail);
        Assert.Empty(_halo.Calls);

        HaloCallsTheWebhookAfterClosing();
        var withClient = await CreateService().RunAsync(3, null, ShortTimeout, CancellationToken.None);

        Assert.True(withClient.Succeeded);
        Assert.Equal(3, _halo.LastClientId);
        Assert.Contains("Integration test alert", _halo.LastTitle);
    }

    [Fact]
    public async Task Run_StopsWithHalosExplanation_WhenCreatingTheTicketFails()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.CreateFailure = new HttpRequestException("HaloPSA returned 400 Bad Request for Post Tickets. Halo said: priority_id is not valid");

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.False(run.Succeeded);
        Assert.Equal(HaloIntegrationTestService.CreateStep, run.Steps.Last().Name);
        Assert.Equal(HaloTestOutcome.Failed, run.Steps.Last().Outcome);
        Assert.Contains("priority_id is not valid", run.Steps.Last().Detail);
        Assert.Equal(["create"], _halo.Calls);
    }

    [Fact]
    public async Task Run_NamesTheTicketToCleanUp_WhenClosingItFails()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.CloseFailure = new HttpRequestException("HaloPSA returned 400 Bad Request for Post Tickets/4242. Halo said: status_id is not valid");

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(HaloIntegrationTestService.CloseStep, step.Name);
        Assert.Equal(HaloTestOutcome.Failed, step.Outcome);
        Assert.Contains("#4242", step.Detail);
        Assert.Contains("by hand", step.Detail);
        Assert.Contains("status_id is not valid", step.Detail);
    }

    [Fact]
    public async Task Run_PointsAtTheAgentSetting_WhenHaloWontCloseAnUnassignedTicket()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.CloseFailure = new HttpRequestException("HaloPSA returned 400 Bad Request for POST Tickets. Halo said: \"Please assign this Ticket these before closing it.\"");

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        var step = run.Steps.Last();
        Assert.Equal(HaloTestOutcome.Failed, step.Outcome);
        Assert.Contains("Please assign this Ticket", step.Detail);
        Assert.Contains("Assign new tickets to", step.Detail);
        Assert.Contains("by hand", step.Detail);
    }

    [Fact]
    public async Task Run_IsNotASuccess_WhenHalosWebhookNeverArrives()
    {
        // A timeout is only a warning, but it must never read as "the whole lifecycle worked".
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(HaloIntegrationTestService.WebhookStep, step.Name);
        Assert.Equal(HaloTestOutcome.Warning, step.Outcome);
        Assert.Contains("No webhook call arrived", step.Detail);
        Assert.Contains("#4242", step.Detail);
    }

    [Fact]
    public async Task Run_FailsAndExplains_WhenTheWebhookReportsADifferentStatus()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        HaloCallsTheWebhookAfterClosing(HaloWebhookDelivery.OtherStatus, statusId: 3);

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(HaloTestOutcome.Failed, step.Outcome);
        Assert.Contains("status 3", step.Detail);
        Assert.Contains("closed status is 9", step.Detail);
    }

    [Theory]
    [InlineData(HaloWebhookDelivery.Unreadable, "ticket_id and status_id")]
    [InlineData(HaloWebhookDelivery.WrongSecret, "wrong secret")]
    public async Task Run_FailsAndExplains_WhenTheWebhookCallCouldNotBeUsed(HaloWebhookDelivery delivery, string expectedInDetail)
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.AfterClose = () => _activity.Record(delivery);

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(HaloTestOutcome.Failed, step.Outcome);
        Assert.Contains(expectedInDetail, step.Detail);
    }

    [Fact]
    public async Task Run_IgnoresAWebhookCallThatArrivedBeforeTheTicketWasClosed()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _activity.Record(HaloWebhookDelivery.ClosedStatus, 4242, 9);
        await Task.Delay(30);

        var run = await CreateService().RunAsync(null, null, ShortTimeout, CancellationToken.None);

        Assert.False(run.Succeeded);
        Assert.Equal(HaloTestOutcome.Warning, run.Steps.Last().Outcome);
    }

    [Fact]
    public async Task Run_ReportsEachStepAsItGoes()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        HaloCallsTheWebhookAfterClosing();
        var reported = new List<(string Name, HaloTestOutcome Outcome)>();

        await CreateService().RunAsync(null, new SynchronousProgress(step => reported.Add((step.Name, step.Outcome))), ShortTimeout, CancellationToken.None);

        // Each network step announces itself as Running before it settles.
        Assert.Contains((HaloIntegrationTestService.CreateStep, HaloTestOutcome.Running), reported);
        Assert.Contains((HaloIntegrationTestService.CreateStep, HaloTestOutcome.Passed), reported);
        Assert.True(reported.IndexOf((HaloIntegrationTestService.CreateStep, HaloTestOutcome.Passed)) < reported.IndexOf((HaloIntegrationTestService.CloseStep, HaloTestOutcome.Running)));
    }

    private sealed class SynchronousProgress(Action<HaloTestStep> onReport) : IProgress<HaloTestStep>
    {
        public void Report(HaloTestStep value) => onReport(value);
    }
}
