using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

/// <summary>The API reads alert rules and the names of destinations. It never returns a destination's URL, and a key limited to some groups
/// gets none of it, as the rules page is closed to them.</summary>
[Collection("Postgres")]
public sealed class AlertRuleEndpointTests : IAsyncLifetime
{
    private const string SecretUrl = "https://example.test/hooks/very-secret-token";

    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public AlertRuleEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _host = await ApiTestHost.StartAsync(_fixture);
        await using var context = _host.CreateContext();
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(int RuleId, int TeamsId, int GroupId)> SeedRuleAsync(string name, bool enabled = true)
    {
        await using var context = _host.CreateContext();
        var group = new Group { Name = $"{name} group" };
        context.Groups.Add(group);
        var teams = await AlertDestinationService.CreateAsync(context, TestActors.Admin, new AlertDestinationInput($"{name} Teams", AlertChannel.Teams, SecretUrl, true));
        var slack = await AlertDestinationService.CreateAsync(context, TestActors.Admin, new AlertDestinationInput($"{name} Slack", AlertChannel.Slack, "https://hooks.slack.com/services/T0/B0/x", true));
        await context.SaveChangesAsync();
        var rule = await AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, new AlertRuleInput(
            name, enabled, "Critical", true, 90, null, new Dictionary<string, string> { [AlertRuleParameterKeys.MissingDays] = "5" },
            [new AlertRuleScopeInput(AlertRuleScopeTarget.Group, group.Id, false)],
            AlertDestinationMode.Selected, [teams.Id], AlertTicketMode.Always,
            AlertQuietMode.Hold, 1320, 420, false, 30, [slack.Id]));
        return (rule.Id, teams.Id, group.Id);
    }

    [Fact]
    public async Task ListRules_ReturnsEveryField_ForAnUnscopedKey()
    {
        var (ruleId, teamsId, groupId) = await SeedRuleAsync("Aurora weekly");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAlertRule>>($"/api/v1/alert-rules?kind={AlertTypes.MissedReport}&pageSize=200");

        var rule = Assert.Single(page!.Items, candidate => candidate.Id == ruleId);
        Assert.Equal(("MissedReport", "Aurora weekly", true, "Critical", true, 90, (int?)null), (rule.Kind, rule.Name, rule.Enabled, rule.Severity, rule.StopProcessing, rule.CooldownMinutes, rule.AutoCloseDays));
        Assert.Equal("5", rule.Parameters[AlertRuleParameterKeys.MissingDays]);
        Assert.Equal(("Selected", "Always"), (rule.DestinationMode, rule.TicketMode));
        Assert.Equal([new ApiNamedRef(teamsId, "Aurora weekly Teams")], rule.Destinations);
        Assert.Equal(new ApiQuietHours("Hold", "22:00", "07:00", false), rule.QuietHours);
        Assert.Equal(30, rule.Escalation.AfterMinutes);
        Assert.Equal(["Aurora weekly Slack"], rule.Escalation.Destinations.Select(destination => destination.Name));
        Assert.Equal([new ApiRuleScopeEntry("Group", groupId, false)], rule.Scope);
        Assert.Contains("Aurora weekly group", rule.AppliesTo);
    }

    [Fact]
    public async Task ListRules_FiltersByKindAndEnabled()
    {
        var (disabledId, _, _) = await SeedRuleAsync("Switched off", enabled: false);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var disabled = await client.GetFromJsonAsync<ApiPage<ApiAlertRule>>("/api/v1/alert-rules?enabled=false&pageSize=200");
        var otherKind = await client.GetFromJsonAsync<ApiPage<ApiAlertRule>>($"/api/v1/alert-rules?kind={AlertTypes.SpfRecordBroken}&pageSize=200");

        Assert.Contains(disabledId, disabled!.Items.Select(rule => rule.Id));
        Assert.All(disabled.Items, rule => Assert.False(rule.Enabled));
        Assert.DoesNotContain(disabledId, otherKind!.Items.Select(rule => rule.Id));
        Assert.All(otherKind.Items, rule => Assert.Equal(AlertTypes.SpfRecordBroken, rule.Kind));
    }

    [Fact]
    public async Task ListRules_RefusesAnUnknownKind()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/alert-rules?kind=NotAKind");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetRule_ReturnsOne_AndA404ForAMissingId()
    {
        var (ruleId, _, _) = await SeedRuleAsync("Single rule");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var rule = await client.GetFromJsonAsync<ApiAlertRule>($"/api/v1/alert-rules/{ruleId}");
        var missing = await client.GetAsync("/api/v1/alert-rules/999999");

        Assert.Equal("Single rule", rule!.Name);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ListDestinations_ReturnsNamesAndKinds_AndNeverTheUrl()
    {
        await SeedRuleAsync("Destination names");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/alert-destinations");
        var body = await response.Content.ReadAsStringAsync();
        var page = await client.GetFromJsonAsync<ApiPage<ApiAlertDestination>>("/api/v1/alert-destinations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("very-secret-token", body);
        Assert.DoesNotContain("hooks.slack.com", body);
        Assert.Contains(page!.Items, destination => destination is { Name: "Destination names Teams", Kind: "Teams", Enabled: true });
    }

    [Fact]
    public async Task AKeyLimitedToAGroup_GetsNoRules_AndAnEmptyDestinationList()
    {
        var (ruleId, _, _) = await SeedRuleAsync("Hidden from scoped");
        var groupId = await _host.SeedGroupAsync("api-rule-scope");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var list = await client.GetAsync("/api/v1/alert-rules");
        var one = await client.GetAsync($"/api/v1/alert-rules/{ruleId}");
        var destinations = await client.GetFromJsonAsync<ApiPage<ApiAlertDestination>>("/api/v1/alert-destinations");

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
        Assert.Empty(destinations!.Items);
        Assert.Equal(0, destinations.TotalCount);
    }

    [Fact]
    public async Task AKeyWithoutAlertsView_IsRefused()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/alert-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/alert-destinations")).StatusCode);
    }
}
