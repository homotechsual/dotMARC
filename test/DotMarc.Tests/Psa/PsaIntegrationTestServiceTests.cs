using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa;

[Collection("Postgres")]
public sealed class PsaIntegrationTestServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(400);

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private readonly HaloWebhookActivity _activity = new(NullLogger<HaloWebhookActivity>.Instance);
    private readonly FakeHalo _halo = new();

    public PsaIntegrationTestServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    /// <summary>The real HaloPSA provider over a fake Halo client, plus any other providers a test adds.</summary>
    private PsaIntegrationTestService CreateService(params IPsaProvider[] otherProviders) =>
        new(new FakeDbContextFactory(_connectionString), [new HaloPsaProvider(_halo), .. otherProviders], _activity, NullLogger<PsaIntegrationTestService>.Instance);

    private Task<PsaTestRun> RunHaloAsync(int? chosenClientId = null, IProgress<PsaTestStep>? progress = null) =>
        CreateService().RunAsync(PsaKind.HaloPsa, chosenClientId?.ToString(System.Globalization.CultureInfo.InvariantCulture), progress, ShortTimeout, CancellationToken.None);

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
        /// <summary>When true, closing is accepted but the ticket keeps its open status.</summary>
        public bool IgnoresCloses { get; set; }
        private bool _closed;

        // 2 is an open status, 9 the closed status the settings use.
        public Task<int> GetTicketStatusAsync(HaloPsaSettings settings, int ticketId, CancellationToken cancellationToken = default) => Task.FromResult(_closed ? 9 : 2);

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

            _closed = !IgnoresCloses;
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
            domain.Groups.Add(new Group { Name = "Contoso", PsaCompanyLinks = [new DotMarc.Psa.PsaCompanyLink { Psa = DotMarc.Psa.PsaKind.HaloPsa, CompanyId = haloClientId.ToString(System.Globalization.CultureInfo.InvariantCulture), CompanyName = "Contoso" }] });
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

        var run = await RunHaloAsync();

        Assert.True(run.Succeeded);
        Assert.Equal(PsaIntegrationTestService.StepNames(PsaKind.HaloPsa), run.Steps.Select(s => s.Name));
        Assert.All(run.Steps, step => Assert.Equal(PsaTestOutcome.Passed, step.Outcome));
        Assert.Equal(["create", "close"], _halo.Calls);
        Assert.Equal(7, _halo.LastClientId);
        Assert.StartsWith("[dotMARC test] ", _halo.LastTitle);
        Assert.Contains("Missing expected DMARC report", _halo.LastTitle);
    }

    [Fact]
    public async Task Run_StopsAtTheSettings_WithoutCallingHalo_WhenTheyAreIncomplete()
    {
        await SeedSettingsAsync(complete: false);

        var run = await RunHaloAsync();

        Assert.False(run.Succeeded);
        var step = Assert.Single(run.Steps);
        Assert.Equal(PsaTestOutcome.Failed, step.Outcome);
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

        var run = await RunHaloAsync();

        Assert.Equal(PsaTestOutcome.Warning, run.Steps[0].Outcome);
        Assert.Contains("switched off", run.Steps[0].Detail);
        Assert.True(run.Succeeded);
    }

    [Fact]
    public async Task Run_AsksForAClient_WhenTheLatestAlertsDomainIsNotMapped()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: null);

        var run = await RunHaloAsync();

        Assert.True(run.NeedsCompanyChoice);
        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(PsaIntegrationTestService.AlertStep, step.Name);
        Assert.Equal(PsaTestOutcome.Failed, step.Outcome);
        Assert.Contains("contoso.io", step.Detail);
        Assert.Empty(_halo.Calls);
    }

    [Fact]
    public async Task Run_UsesTheChosenClient_WhenTheDomainIsNotMapped()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: null);
        HaloCallsTheWebhookAfterClosing();

        var run = await RunHaloAsync(12);

        Assert.True(run.Succeeded);
        Assert.Equal(12, _halo.LastClientId);
        var alertStep = run.Steps.Single(s => s.Name == PsaIntegrationTestService.AlertStep);
        Assert.Equal(PsaTestOutcome.Warning, alertStep.Outcome);
        Assert.Contains("no Halo client mapped", alertStep.Detail);
    }

    [Fact]
    public async Task Run_AsksForAClient_WhenThereAreNoAlertsYet_AndUsesASampleOnceOneIsChosen()
    {
        await SeedSettingsAsync();

        var withoutClient = await RunHaloAsync();
        Assert.True(withoutClient.NeedsCompanyChoice);
        Assert.Contains("no alerts yet", withoutClient.Steps.Last().Detail);
        Assert.Empty(_halo.Calls);

        HaloCallsTheWebhookAfterClosing();
        var withClient = await RunHaloAsync(3);

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

        var run = await RunHaloAsync();

        Assert.False(run.Succeeded);
        Assert.Equal(PsaIntegrationTestService.CreateStep, run.Steps.Last().Name);
        Assert.Equal(PsaTestOutcome.Failed, run.Steps.Last().Outcome);
        Assert.Contains("priority_id is not valid", run.Steps.Last().Detail);
        Assert.Equal(["create"], _halo.Calls);
    }

    [Fact]
    public async Task Run_NamesTheTicketToCleanUp_WhenClosingItFails()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.CloseFailure = new HttpRequestException("HaloPSA returned 400 Bad Request for Post Tickets/4242. Halo said: status_id is not valid");

        var run = await RunHaloAsync();

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(PsaIntegrationTestService.CloseStep, step.Name);
        Assert.Equal(PsaTestOutcome.Failed, step.Outcome);
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

        var run = await RunHaloAsync();

        var step = run.Steps.Last();
        Assert.Equal(PsaTestOutcome.Failed, step.Outcome);
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

        var run = await RunHaloAsync();

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(PsaIntegrationTestService.WebhookStep, step.Name);
        Assert.Equal(PsaTestOutcome.Warning, step.Outcome);
        Assert.Contains("No webhook call arrived", step.Detail);
        Assert.Contains("#4242", step.Detail);
    }

    [Fact]
    public async Task Run_FailsAndExplains_WhenTheWebhookReportsADifferentStatus()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        HaloCallsTheWebhookAfterClosing(HaloWebhookDelivery.OtherStatus, statusId: 3);

        var run = await RunHaloAsync();

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(PsaTestOutcome.Failed, step.Outcome);
        Assert.Contains("status 3", step.Detail);
        Assert.Contains("closed status is 9", step.Detail);
    }

    [Theory]
    [InlineData(HaloWebhookDelivery.Unreadable, "couldn't find the ticket in the body")]
    [InlineData(HaloWebhookDelivery.WrongSecret, "wrong secret")]
    public async Task Run_FailsAndExplains_WhenTheWebhookCallCouldNotBeUsed(HaloWebhookDelivery delivery, string expectedInDetail)
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.AfterClose = () => _activity.Record(delivery);

        var run = await RunHaloAsync();

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal(PsaTestOutcome.Failed, step.Outcome);
        Assert.Contains(expectedInDetail, step.Detail);
    }

    [Fact]
    public async Task Run_ShowsTheFieldNamesHaloSent_WhenTheWebhookBodyHadNoTicket()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.AfterClose = () => _activity.Record(HaloWebhookDelivery.Unreadable, detail: "Halo sent: id, event, webhook_id");

        var run = await RunHaloAsync();

        Assert.Contains("Halo sent: id, event, webhook_id", run.Steps.Last().Detail);
    }

    [Fact]
    public async Task Run_ExplainsAWebhookThatNamedTheTicketButNotItsStatus()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.AfterClose = () => _activity.Record(HaloWebhookDelivery.StatusUnknown, 4242, detail: "HaloPSA returned 401 Unauthorized for GET Tickets/4242.");

        var run = await RunHaloAsync();

        var step = run.Steps.Last();
        Assert.Equal(PsaTestOutcome.Failed, step.Outcome);
        Assert.Contains("without saying its status", step.Detail);
        Assert.Contains("401 Unauthorized", step.Detail);
    }

    [Fact]
    public async Task Run_IgnoresAWebhookCallThatArrivedBeforeTheTicketWasClosed()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _activity.Record(HaloWebhookDelivery.ClosedStatus, 4242, 9);
        await Task.Delay(30);

        var run = await RunHaloAsync();

        Assert.False(run.Succeeded);
        Assert.Equal(PsaTestOutcome.Warning, run.Steps.Last().Outcome);
    }

    [Fact]
    public async Task Run_ReportsEachStepAsItGoes()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        HaloCallsTheWebhookAfterClosing();
        var reported = new List<(string Name, PsaTestOutcome Outcome)>();

        await RunHaloAsync(progress: new SynchronousProgress(step => reported.Add((step.Name, step.Outcome))));

        // Each network step announces itself as Running before it settles.
        Assert.Contains((PsaIntegrationTestService.CreateStep, PsaTestOutcome.Running), reported);
        Assert.Contains((PsaIntegrationTestService.CreateStep, PsaTestOutcome.Passed), reported);
        Assert.True(reported.IndexOf((PsaIntegrationTestService.CreateStep, PsaTestOutcome.Passed)) < reported.IndexOf((PsaIntegrationTestService.CloseStep, PsaTestOutcome.Running)));
    }

    [Fact]
    public async Task ConnectWise_PassesWhenTheTicketReadsOpenThenClosed_WithNoWebhookStep()
    {
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise);

        var run = await CreateService(connectWise).RunAsync(PsaKind.ConnectWise, "250", null, null, CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.Equal(PsaIntegrationTestService.StepNames(PsaKind.ConnectWise), run.Steps.Select(step => step.Name));
        Assert.DoesNotContain(run.Steps, step => step.Name == PsaIntegrationTestService.WebhookStep);
        Assert.Equal("250", Assert.Single(connectWise.Created).CompanyId);
    }

    [Fact]
    public async Task ATicketThatStillReadsOpenAfterClosing_FailsTheLastStep()
    {
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { IgnoreCloses = true };

        var run = await CreateService(connectWise).RunAsync(PsaKind.ConnectWise, "250", null, null, CancellationToken.None);

        Assert.False(run.Succeeded);
        var step = run.Steps.Last();
        Assert.Equal((PsaIntegrationTestService.ClosedStep, PsaTestOutcome.Failed), (step.Name, step.Outcome));
        Assert.Contains("still reads as open", step.Detail);
    }

    [Fact]
    public async Task ANewTicketThatAlreadyReadsClosed_FailsTheOpenStep()
    {
        await SeedSettingsAsync();
        await SeedAlertAsync(mappedHaloClientId: 7);
        _halo.IgnoresCloses = false;
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise);
        connectWise.States["1000"] = PsaTicketState.Closed;
        connectWise.NextTicketNumber = 1000;

        var run = await CreateService(new ClosedOnArrival(connectWise)).RunAsync(PsaKind.ConnectWise, "250", null, null, CancellationToken.None);

        var step = run.Steps.Last();
        Assert.Equal((PsaIntegrationTestService.OpenStep, PsaTestOutcome.Failed), (step.Name, step.Outcome));
        Assert.Contains("closed status", step.Detail);
    }

    [Fact]
    public async Task AnUnknownPsa_FailsTheSettingsStep()
    {
        var run = await CreateService().RunAsync(PsaKind.Autotask, "1", null, null, CancellationToken.None);

        Assert.False(run.Succeeded);
        Assert.Equal(PsaTestOutcome.Failed, Assert.Single(run.Steps).Outcome);
    }

    [Fact]
    public async Task Halo_WithoutAWebhookSecret_FailsTheSettingsStep()
    {
        // The webhook secret isn't needed for ticketing, but the Halo test waits for the webhook, so it needs one.
        await SeedSettingsAsync();
        await using (var context = CreateContext())
        {
            var settings = await context.HaloPsaSettings.SingleAsync();
            settings.WebhookSecret = null;
            await context.SaveChangesAsync();
        }

        var run = await RunHaloAsync(7);

        var step = Assert.Single(run.Steps);
        Assert.Equal(PsaTestOutcome.Failed, step.Outcome);
        Assert.Contains("webhook secret", step.Detail);
    }

    /// <summary>A provider whose new tickets already read as closed, as when the closed status is also the one new
    /// tickets start in.</summary>
    private sealed class ClosedOnArrival(FakePsaProvider inner) : IPsaProvider
    {
        public PsaKind Kind => inner.Kind;
        public Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) => inner.GetReadinessAsync(context, cancellationToken);
        public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) => inner.ListCompaniesAsync(context, cancellationToken);
        public Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default) => inner.CreateTicketAsync(context, request, cancellationToken);
        public Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default) => Task.FromResult(PsaTicketState.Closed);
        public Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default) => inner.CloseTicketAsync(context, ticketId, note, cancellationToken);
        public Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) => inner.GetTicketUrlTemplateAsync(context, cancellationToken);
    }

    private sealed class SynchronousProgress(Action<PsaTestStep> onReport) : IProgress<PsaTestStep>
    {
        public void Report(PsaTestStep value) => onReport(value);
    }
}
