using System.Text.Json;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class MetricAlertKindsTests : IAsyncLifetime
{
    private static readonly string[] MetricKinds = [AlertTypes.PassRateLow, AlertTypes.VolumeChanged, AlertTypes.RejectedMessagesHigh];

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public MetricAlertKindsTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString)
            .ConfigureWarnings(warnings => warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning)).Options);

    private static AlertRuleParameterDefinition Parameter(string kind, string key) =>
        AlertRuleKinds.Find(kind)!.Parameters.Single(definition => definition.Key == key);

    private static AlertRuleInput InputWith(string kind, string key, string value)
    {
        var parameters = AlertRuleKinds.Find(kind)!.Parameters.ToDictionary(definition => definition.Key, definition => definition.Default);
        parameters[key] = value;
        return new AlertRuleInput("Metric rule", true, "Warning", true, null, null, parameters, []);
    }

    // ---- Catalogue ----

    [Theory]
    [InlineData(AlertTypes.PassRateLow, "belowPercent", 1, 99, "90")]
    [InlineData(AlertTypes.PassRateLow, "windowDays", 1, 30, "7")]
    [InlineData(AlertTypes.PassRateLow, "minimumMessages", 1, 1000000, "100")]
    [InlineData(AlertTypes.VolumeChanged, "changePercent", 10, 500, "50")]
    [InlineData(AlertTypes.VolumeChanged, "windowDays", 1, 15, "7")]
    [InlineData(AlertTypes.VolumeChanged, "minimumMessages", 1, 1000000, "100")]
    [InlineData(AlertTypes.RejectedMessagesHigh, "messagesAtLeast", 1, 1000000, "50")]
    [InlineData(AlertTypes.RejectedMessagesHigh, "windowDays", 1, 30, "7")]
    public void EachIntegerParameter_HasItsRangeAndDefault(string kind, string key, int minimum, int maximum, string defaultValue)
    {
        var definition = Parameter(kind, key);

        Assert.Equal(AlertRuleParameterType.Integer, definition.Type);
        Assert.Equal((minimum, maximum, defaultValue), (definition.Min, definition.Max, definition.Default));
    }

    [Fact]
    public void VolumeDirection_IsAChoiceOfDropRiseOrEither_DefaultingToDrop()
    {
        var definition = Parameter(AlertTypes.VolumeChanged, AlertRuleParameterKeys.Direction);

        Assert.Equal(AlertRuleParameterType.Choice, definition.Type);
        Assert.Equal("drop", definition.Default);
        Assert.Equal(["drop", "rise", "either"], definition.Choices!.Select(choice => choice.Value));
    }

    [Fact]
    public void TheThreeKinds_HaveTheirOwnNamesAndParameterSets()
    {
        Assert.Equal("DMARC pass rate low", AlertRuleKinds.Find(AlertTypes.PassRateLow)!.DisplayName);
        Assert.Equal("Message volume changed", AlertRuleKinds.Find(AlertTypes.VolumeChanged)!.DisplayName);
        Assert.Equal("Rejected or quarantined messages high", AlertRuleKinds.Find(AlertTypes.RejectedMessagesHigh)!.DisplayName);
        Assert.Equal(["belowPercent", "windowDays", "minimumMessages"], AlertRuleKinds.Find(AlertTypes.PassRateLow)!.Parameters.Select(definition => definition.Key));
        Assert.Equal(["direction", "changePercent", "windowDays", "minimumMessages"], AlertRuleKinds.Find(AlertTypes.VolumeChanged)!.Parameters.Select(definition => definition.Key));
        Assert.Equal(["messagesAtLeast", "windowDays"], AlertRuleKinds.Find(AlertTypes.RejectedMessagesHigh)!.Parameters.Select(definition => definition.Key));
    }

    [Fact]
    public void TheThreeKinds_AreDomainScoped_WarningSeverity_OffByDefault_AndOpenNoTicketsByDefault()
    {
        Assert.All(MetricKinds, key =>
        {
            var kind = AlertRuleKinds.Find(key)!;
            Assert.True(kind.HasDomainScope);
            Assert.False(kind.HasAutoClose);
            Assert.Equal(AlertSeverities.Warning, kind.DefaultSeverity);
            Assert.False(kind.EnabledByDefault);
            Assert.False(AlertTypes.Find(key)!.CreatesTicketByDefault);
        });
    }

    [Fact]
    public void EveryEarlierKind_StaysOnByDefault()
    {
        Assert.All(AlertRuleKinds.All.Where(kind => !MetricKinds.Contains(kind.Key)), kind => Assert.True(kind.EnabledByDefault));
    }

    // ---- Parameter accessors ----

    [Fact]
    public void Text_ReturnsTheRulesValue_OrTheKindsDefault()
    {
        var rule = new AlertRule { Kind = AlertTypes.VolumeChanged, Name = "Volume", Severity = AlertSeverities.Warning };
        var withValue = new ResolvedAlertRule(rule, new AlertRuleParameters("{\"direction\":\"rise\"}"));
        var withoutValue = new ResolvedAlertRule(rule, new AlertRuleParameters(null));

        Assert.Equal("rise", withValue.Text(AlertRuleParameterKeys.Direction));
        Assert.Equal("drop", withoutValue.Text(AlertRuleParameterKeys.Direction));
    }

    // ---- Validation ----

    [Theory]
    [InlineData(AlertTypes.PassRateLow, "belowPercent", "0")]
    [InlineData(AlertTypes.PassRateLow, "belowPercent", "100")]
    [InlineData(AlertTypes.PassRateLow, "windowDays", "0")]
    [InlineData(AlertTypes.PassRateLow, "windowDays", "31")]
    [InlineData(AlertTypes.PassRateLow, "minimumMessages", "0")]
    [InlineData(AlertTypes.PassRateLow, "minimumMessages", "1000001")]
    [InlineData(AlertTypes.VolumeChanged, "changePercent", "9")]
    [InlineData(AlertTypes.VolumeChanged, "changePercent", "501")]
    [InlineData(AlertTypes.VolumeChanged, "windowDays", "16")]
    [InlineData(AlertTypes.VolumeChanged, "minimumMessages", "0")]
    [InlineData(AlertTypes.RejectedMessagesHigh, "messagesAtLeast", "0")]
    [InlineData(AlertTypes.RejectedMessagesHigh, "windowDays", "31")]
    public void AValueJustOutsideItsRange_IsRefused_WithAMessageNamingTheSetting(string kind, string key, string value)
    {
        var problems = AlertRuleValidator.Problems(kind, InputWith(kind, key, value));

        var problem = Assert.Single(problems);
        Assert.Contains(Parameter(kind, key).Label, problem);
    }

    [Fact]
    public void ADirectionOutsideTheChoices_IsRefused()
    {
        var problems = AlertRuleValidator.Problems(AlertTypes.VolumeChanged, InputWith(AlertTypes.VolumeChanged, AlertRuleParameterKeys.Direction, "sideways"));

        Assert.Contains(problems, problem => problem.Contains(Parameter(AlertTypes.VolumeChanged, AlertRuleParameterKeys.Direction).Label));
    }

    [Theory]
    [InlineData(AlertTypes.PassRateLow)]
    [InlineData(AlertTypes.VolumeChanged)]
    [InlineData(AlertTypes.RejectedMessagesHigh)]
    public void TheDefaults_AreValid(string kind)
    {
        var parameters = AlertRuleKinds.Find(kind)!.Parameters.ToDictionary(definition => definition.Key, definition => definition.Default);

        Assert.Empty(AlertRuleValidator.Problems(kind, new AlertRuleInput("Metric rule", true, "Warning", true, null, null, parameters, [])));
    }

    // ---- Seeding ----

    [Fact]
    public async Task Seeding_CreatesTheThreeRulesSwitchedOff_WithEveryParameterStored()
    {
        await using var context = CreateContext();

        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);

        var rules = await context.AlertRules.AsNoTracking().Where(rule => MetricKinds.Contains(rule.Kind)).ToListAsync();
        Assert.Equal(3, rules.Count);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.Enabled);
            Assert.Equal(rule.Kind, rule.DefaultKey);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ParametersJson!)!;
            Assert.Equal(AlertRuleKinds.Find(rule.Kind)!.Parameters.Select(definition => definition.Key).Order(), stored.Keys.Order());
        });
    }

    [Fact]
    public async Task OnAnInstallWithTheEarlierPack_OnlyTheThreeAreAdded_AndTheOthersAreUntouched()
    {
        await using (var context = CreateContext())
        {
            await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
            var earlier = await context.AlertRules.Where(rule => rule.Kind == AlertTypes.MissedReport).SingleAsync();
            earlier.Name = "Renamed by an admin";
            await context.AlertRules.Where(rule => MetricKinds.Contains(rule.Kind)).ExecuteDeleteAsync();
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var rules = await verify.AlertRules.AsNoTracking().ToListAsync();
        Assert.Equal(AlertRuleKinds.All.Count, rules.Count);
        Assert.Equal("Renamed by an admin", rules.Single(rule => rule.Kind == AlertTypes.MissedReport).Name);
        Assert.All(rules.Where(rule => MetricKinds.Contains(rule.Kind)), rule => Assert.False(rule.Enabled));
    }

    // ---- Reset ----

    [Theory]
    [InlineData(AlertTypes.PassRateLow)]
    [InlineData(AlertTypes.VolumeChanged)]
    [InlineData(AlertTypes.RejectedMessagesHigh)]
    public async Task ResettingAMetricRuleThatWasSwitchedOn_LeavesItOff(string kind)
    {
        int ruleId;
        await using (var context = CreateContext())
        {
            await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
            var rule = await context.AlertRules.SingleAsync(candidate => candidate.DefaultKey == kind);
            rule.Enabled = true;
            await context.SaveChangesAsync();
            ruleId = rule.Id;
        }

        await using (var context = CreateContext())
        {
            await AlertRuleService.ResetToDefaultAsync(context, TestActors.Admin, ruleId);
        }

        await using var verify = CreateContext();
        Assert.False((await verify.AlertRules.AsNoTracking().SingleAsync(rule => rule.Id == ruleId)).Enabled);
    }
}
