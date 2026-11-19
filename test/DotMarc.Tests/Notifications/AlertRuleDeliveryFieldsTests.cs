using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The destination and ticket-mode fields of an alert rule: stored, validated, audited and reset.</summary>
[Collection("Postgres")]
public sealed class AlertRuleDeliveryFieldsTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private int _teamsId;
    private int _slackId;

    public AlertRuleDeliveryFieldsTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
        var teams = await AlertDestinationService.CreateAsync(context, TestActors.Admin, new AlertDestinationInput("Shared Teams", AlertChannel.Teams, "https://example.test/teams", true));
        var slack = await AlertDestinationService.CreateAsync(context, TestActors.Admin, new AlertDestinationInput("Aurora Slack", AlertChannel.Slack, "https://hooks.slack.com/services/T0/B0/x", true));
        (_teamsId, _slackId) = (teams.Id, slack.Id);
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

    private static AlertRuleInput Input(
        string name = "Aurora weekly", AlertDestinationMode mode = AlertDestinationMode.All, int[]? destinationIds = null, AlertTicketMode ticketMode = AlertTicketMode.FollowRules) =>
        new(name, true, "Warning", true, null, null, new Dictionary<string, string>(), [], mode, destinationIds ?? [], ticketMode);

    private async Task<AlertRule> CreateRuleAsync(AlertRuleInput input)
    {
        await using var context = CreateContext();
        return await AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, input);
    }

    private async Task<AlertRule> ReloadAsync(int ruleId)
    {
        await using var context = CreateContext();
        return await context.AlertRules.AsNoTracking().Include(rule => rule.Destinations).SingleAsync(rule => rule.Id == ruleId);
    }

    private async Task<List<AuditEntry>> AuditAsync(string action)
    {
        await using var context = CreateContext();
        return await context.AuditEntries.AsNoTracking().Where(entry => entry.Action == action).OrderBy(entry => entry.Id).ToListAsync();
    }

    [Fact]
    public async Task TheDefaultPack_SendsToAllDestinations_AndFollowsTheTicketRules()
    {
        await using var context = CreateContext();

        var rules = await context.AlertRules.AsNoTracking().Where(rule => rule.DefaultKey != null).ToListAsync();

        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal((AlertDestinationMode.All, AlertTicketMode.FollowRules), (rule.DestinationMode, rule.TicketMode)));
    }

    [Fact]
    public async Task Create_WithSelectedDestinations_StoresTheLinksAndTheTicketMode()
    {
        var created = await CreateRuleAsync(Input(mode: AlertDestinationMode.Selected, destinationIds: [_teamsId, _slackId], ticketMode: AlertTicketMode.Always));

        var stored = await ReloadAsync(created.Id);
        Assert.Equal(AlertDestinationMode.Selected, stored.DestinationMode);
        Assert.Equal(AlertTicketMode.Always, stored.TicketMode);
        Assert.Equal(new[] { _teamsId, _slackId }.Order(), stored.Destinations.Select(link => link.AlertDestinationId).Order());
    }

    [Fact]
    public async Task Create_WithAllMode_IgnoresAndClearsAnySelection()
    {
        var created = await CreateRuleAsync(Input(mode: AlertDestinationMode.All, destinationIds: [_teamsId]));

        Assert.Empty((await ReloadAsync(created.Id)).Destinations);
    }

    [Fact]
    public async Task Create_WithSelectedMode_AndNoDestinations_IsAllowedAndQuiet()
    {
        var created = await CreateRuleAsync(Input(mode: AlertDestinationMode.Selected));

        var stored = await ReloadAsync(created.Id);
        Assert.Equal(AlertDestinationMode.Selected, stored.DestinationMode);
        Assert.Empty(stored.Destinations);
    }

    [Fact]
    public async Task Create_RefusesADestinationThatDoesNotExist_NamingTheProblem()
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, Input(mode: AlertDestinationMode.Selected, destinationIds: [9999])));

        Assert.Contains("destination", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await AuditAsync(AuditActions.AlertRuleCreated));
    }

    [Fact]
    public async Task Update_ReplacesTheSelection_AndAuditsDestinationNamesTicketModeAndNeverAUrl()
    {
        var created = await CreateRuleAsync(Input(mode: AlertDestinationMode.Selected, destinationIds: [_teamsId]));

        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, created.Id, Input(mode: AlertDestinationMode.Selected, destinationIds: [_slackId], ticketMode: AlertTicketMode.Never));
        }

        var stored = await ReloadAsync(created.Id);
        Assert.Equal([_slackId], stored.Destinations.Select(link => link.AlertDestinationId));
        var entry = Assert.Single(await AuditAsync(AuditActions.AlertRuleChanged));
        Assert.Contains(entry.Changes, change => change is { Field: "Ticket mode", Old: "FollowRules", New: "Never" });
        var destinations = Assert.Single(entry.Changes, change => change.Field == "Destinations");
        Assert.Contains("Aurora Slack", destinations.New);
        Assert.DoesNotContain(entry.Changes, change => (change.Old ?? "").Contains("example.test") || (change.New ?? "").Contains("example.test"));
    }

    [Fact]
    public async Task Update_FromSelectedToAll_DropsTheLinks()
    {
        var created = await CreateRuleAsync(Input(mode: AlertDestinationMode.Selected, destinationIds: [_teamsId]));

        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, created.Id, Input(mode: AlertDestinationMode.All));
        }

        var stored = await ReloadAsync(created.Id);
        Assert.Equal(AlertDestinationMode.All, stored.DestinationMode);
        Assert.Empty(stored.Destinations);
    }

    [Fact]
    public async Task Update_WithNothingChanged_WritesNoAuditEntry()
    {
        var created = await CreateRuleAsync(Input(mode: AlertDestinationMode.Selected, destinationIds: [_teamsId, _slackId], ticketMode: AlertTicketMode.Always));

        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, created.Id, Input(mode: AlertDestinationMode.Selected, destinationIds: [_slackId, _teamsId], ticketMode: AlertTicketMode.Always));
        }

        Assert.Empty(await AuditAsync(AuditActions.AlertRuleChanged));
    }

    [Fact]
    public async Task Reset_PutsADefaultRuleBackToAllDestinationsAndTheTicketRules()
    {
        int defaultRuleId;
        await using (var context = CreateContext())
        {
            var defaultRule = await context.AlertRules.Include(rule => rule.Scope).SingleAsync(rule => rule.DefaultKey == AlertTypes.MissedReport);
            defaultRuleId = defaultRule.Id;
            defaultRule.DestinationMode = AlertDestinationMode.Selected;
            defaultRule.TicketMode = AlertTicketMode.Never;
            defaultRule.Destinations.Add(new AlertRuleDestination { AlertDestinationId = _teamsId });
            await context.SaveChangesAsync();
            await AlertRuleService.ResetToDefaultAsync(context, TestActors.Admin, defaultRuleId);
        }

        var stored = await ReloadAsync(defaultRuleId);
        Assert.Equal((AlertDestinationMode.All, AlertTicketMode.FollowRules), (stored.DestinationMode, stored.TicketMode));
        Assert.Empty(stored.Destinations);
    }

    [Fact]
    public void ToInput_CarriesTheDestinationsAndTheTicketMode()
    {
        var rule = new AlertRule
        {
            Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning", DestinationMode = AlertDestinationMode.Selected, TicketMode = AlertTicketMode.Always,
            Destinations = [new AlertRuleDestination { AlertDestinationId = 4 }, new AlertRuleDestination { AlertDestinationId = 9 }]
        };

        var input = AlertRulePresentation.ToInput(rule);

        Assert.Equal((AlertDestinationMode.Selected, AlertTicketMode.Always), (input.DestinationMode, input.TicketMode));
        Assert.Equal([4, 9], input.DestinationIds!.Order());
    }
}
