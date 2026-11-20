using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The quiet hours and escalation fields of an alert rule: validated, stored, audited and reset. The database context throws on
/// loading two collections without a split query, as the app does.</summary>
[Collection("Postgres")]
public sealed class AlertRuleQuietFieldsTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private int _teamsId;
    private int _slackId;

    public AlertRuleQuietFieldsTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
        var teams = await AlertDestinationService.CreateAsync(context, TestActors.Admin, new AlertDestinationInput("Shared Teams", AlertChannel.Teams, "https://example.test/teams-secret", true));
        var slack = await AlertDestinationService.CreateAsync(context, TestActors.Admin, new AlertDestinationInput("On-call Slack", AlertChannel.Slack, "https://hooks.slack.com/services/T0/B0/x", true));
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
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString)
            .ConfigureWarnings(warnings => warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning)).Options);

    private static AlertRuleInput Input(
        AlertQuietMode quietMode = AlertQuietMode.Off, int? start = null, int? end = null, bool ignoredForCritical = true,
        int? escalateAfter = null, int[]? escalationIds = null, string name = "Aurora weekly") =>
        new(name, true, "Warning", true, null, null, new Dictionary<string, string>(), [],
            QuietMode: quietMode, QuietStartMinutes: start, QuietEndMinutes: end, QuietIgnoredForCritical: ignoredForCritical,
            EscalateAfterMinutes: escalateAfter, EscalationDestinationIds: escalationIds ?? []);

    private async Task<AlertRule> CreateRuleAsync(AlertRuleInput input)
    {
        await using var context = CreateContext();
        return await AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, input);
    }

    private async Task<AlertRule> ReloadAsync(int ruleId)
    {
        await using var context = CreateContext();
        return await context.AlertRules.AsNoTracking().Include(rule => rule.EscalationDestinations).SingleAsync(rule => rule.Id == ruleId);
    }

    private async Task<List<AuditEntry>> AuditAsync(string action)
    {
        await using var context = CreateContext();
        return await context.AuditEntries.AsNoTracking().Where(entry => entry.Action == action).OrderBy(entry => entry.Id).ToListAsync();
    }

    // ---- validation ----

    [Theory]
    [InlineData(null, 420)]
    [InlineData(1320, null)]
    [InlineData(null, null)]
    public async Task AQuietModeNeedsBothTimes(int? start, int? end)
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, Input(AlertQuietMode.Hold, start, end)));

        Assert.Contains("Quiet hours", exception.Message);
    }

    [Theory]
    [InlineData(-1, 420)]
    [InlineData(1320, 1440)]
    [InlineData(1500, 60)]
    public async Task QuietTimesOutsideTheDay_AreRefused(int start, int end)
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, Input(AlertQuietMode.Hold, start, end)));

        Assert.Contains("0:00", exception.Message);
    }

    [Fact]
    public async Task AStartEqualToTheEnd_IsRefused()
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, Input(AlertQuietMode.Mute, 480, 480)));

        Assert.Contains("different", exception.Message);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(10081)]
    public async Task AnEscalationTimeOutsideTheRange_IsRefused(int minutes)
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, Input(escalateAfter: minutes)));

        Assert.Contains("Escalate after", exception.Message);
    }

    [Fact]
    public async Task AnUnknownEscalationDestination_IsRefused()
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, Input(escalateAfter: 30, escalationIds: [9999])));

        Assert.Contains("destination", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await AuditAsync(AuditActions.AlertRuleCreated));
    }

    // ---- storing ----

    [Fact]
    public async Task Create_StoresTheWindowTheCriticalSwitchAndTheEscalation()
    {
        var created = await CreateRuleAsync(Input(AlertQuietMode.Hold, 1320, 420, ignoredForCritical: false, escalateAfter: 45, escalationIds: [_slackId, _teamsId]));

        var stored = await ReloadAsync(created.Id);
        Assert.Equal((AlertQuietMode.Hold, (int?)1320, (int?)420, false, (int?)45), (stored.QuietMode, stored.QuietStartMinutes, stored.QuietEndMinutes, stored.QuietIgnoredForCritical, stored.EscalateAfterMinutes));
        Assert.Equal(new[] { _teamsId, _slackId }.Order(), stored.EscalationDestinations.Select(link => link.AlertDestinationId).Order());
    }

    [Fact]
    public async Task WithNoEscalationTime_TheEscalationDestinationsAreNotKept()
    {
        var created = await CreateRuleAsync(Input(escalateAfter: null, escalationIds: [_slackId]));

        Assert.Empty((await ReloadAsync(created.Id)).EscalationDestinations);
    }

    [Fact]
    public async Task Update_ReplacesTheEscalationDestinations_AndAuditsEachChangedFieldByNameNeverAUrl()
    {
        var created = await CreateRuleAsync(Input(escalateAfter: 30, escalationIds: [_teamsId]));

        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, created.Id, Input(AlertQuietMode.Mute, 600, 720, ignoredForCritical: false, escalateAfter: 60, escalationIds: [_slackId]));
        }

        var stored = await ReloadAsync(created.Id);
        Assert.Equal([_slackId], stored.EscalationDestinations.Select(link => link.AlertDestinationId));
        var entry = Assert.Single(await AuditAsync(AuditActions.AlertRuleChanged));
        Assert.Contains(entry.Changes, change => change is { Field: "Quiet mode", Old: "Off", New: "Mute" });
        Assert.Contains(entry.Changes, change => change is { Field: "Quiet hours", Old: "None", New: "10:00 to 12:00" });
        Assert.Contains(entry.Changes, change => change.Field == "Critical alerts ignore quiet hours");
        Assert.Contains(entry.Changes, change => change is { Field: "Escalate after (minutes)", Old: "30", New: "60" });
        var destinations = Assert.Single(entry.Changes, change => change.Field == "Escalation destinations");
        Assert.Contains("On-call Slack", destinations.New);
        Assert.DoesNotContain(entry.Changes, change => (change.Old ?? "").Contains("example.test") || (change.New ?? "").Contains("example.test"));
    }

    [Fact]
    public async Task Update_WithNothingChanged_WritesNoAuditEntry()
    {
        var created = await CreateRuleAsync(Input(AlertQuietMode.Hold, 1320, 420, escalateAfter: 30, escalationIds: [_slackId, _teamsId]));

        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, created.Id, Input(AlertQuietMode.Hold, 1320, 420, escalateAfter: 30, escalationIds: [_teamsId, _slackId]));
        }

        Assert.Empty(await AuditAsync(AuditActions.AlertRuleChanged));
    }

    [Fact]
    public async Task Reset_PutsADefaultRuleBackToNoQuietHoursAndNoEscalation()
    {
        int defaultRuleId;
        await using (var context = CreateContext())
        {
            var defaultRule = await context.AlertRules.Include(rule => rule.Scope).Include(rule => rule.Destinations).Include(rule => rule.EscalationDestinations).AsSplitQuery()
                .SingleAsync(rule => rule.DefaultKey == AlertTypes.MissedReport);
            defaultRuleId = defaultRule.Id;
            (defaultRule.QuietMode, defaultRule.QuietStartMinutes, defaultRule.QuietEndMinutes, defaultRule.QuietIgnoredForCritical, defaultRule.EscalateAfterMinutes) = (AlertQuietMode.Hold, 60, 120, false, 30);
            defaultRule.EscalationDestinations.Add(new AlertRuleEscalationDestination { AlertDestinationId = _slackId });
            await context.SaveChangesAsync();
            await AlertRuleService.ResetToDefaultAsync(context, TestActors.Admin, defaultRuleId);
        }

        var stored = await ReloadAsync(defaultRuleId);
        Assert.Equal((AlertQuietMode.Off, (int?)null, (int?)null, true, (int?)null), (stored.QuietMode, stored.QuietStartMinutes, stored.QuietEndMinutes, stored.QuietIgnoredForCritical, stored.EscalateAfterMinutes));
        Assert.Empty(stored.EscalationDestinations);
    }

    [Fact]
    public void ToInput_CarriesTheQuietHoursAndTheEscalation()
    {
        var rule = new AlertRule
        {
            Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning", QuietMode = AlertQuietMode.Mute, QuietStartMinutes = 60, QuietEndMinutes = 120,
            QuietIgnoredForCritical = false, EscalateAfterMinutes = 15,
            EscalationDestinations = [new AlertRuleEscalationDestination { AlertDestinationId = 4 }]
        };

        var input = AlertRulePresentation.ToInput(rule);

        Assert.Equal((AlertQuietMode.Mute, (int?)60, (int?)120, false, (int?)15), (input.QuietMode, input.QuietStartMinutes, input.QuietEndMinutes, input.QuietIgnoredForCritical, input.EscalateAfterMinutes));
        Assert.Equal([4], input.EscalationDestinationIds);
    }

    [Theory]
    [InlineData(AlertQuietMode.Hold, 1320, 420, "Quiet 22:00 to 07:00 (held).")]
    [InlineData(AlertQuietMode.Mute, 540, 1020, "Quiet 09:00 to 17:00 (muted).")]
    public void TheRowSummary_ReadsTheQuietWindow(AlertQuietMode mode, int start, int end, string expected)
    {
        var rule = new AlertRule { Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning", QuietMode = mode, QuietStartMinutes = start, QuietEndMinutes = end };

        Assert.Equal(expected, AlertRulePresentation.DescribeDelivery(rule, new Dictionary<int, string>()));
    }

    [Fact]
    public void TheRowSummary_ReadsTheEscalation_AfterTheOtherSentences()
    {
        var rule = new AlertRule
        {
            Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning", TicketMode = AlertTicketMode.Never,
            QuietMode = AlertQuietMode.Hold, QuietStartMinutes = 1320, QuietEndMinutes = 420, EscalateAfterMinutes = 30
        };

        Assert.Equal("Never opens a ticket. Quiet 22:00 to 07:00 (held). Escalates after 30 minutes.", AlertRulePresentation.DescribeDelivery(rule, new Dictionary<int, string>()));
    }

    [Fact]
    public void TheRowSummary_SaysNothingForAnOffWindow()
    {
        var rule = new AlertRule { Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning", QuietMode = AlertQuietMode.Off, QuietStartMinutes = 60, QuietEndMinutes = 120 };

        Assert.Null(AlertRulePresentation.DescribeDelivery(rule, new Dictionary<int, string>()));
    }
}
