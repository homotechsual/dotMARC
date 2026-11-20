using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Demo;

/// <summary>The demo shows alert rules: the default pack, plus a rule scoped to the Aurora Retail group and one that nags about senders left
/// Under review for Cobalt Freight.</summary>
[Collection("Postgres")]
public sealed class DemoAlertRulesTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DemoAlertRulesTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task SeedDemoAsync()
    {
        await using var context = CreateContext();
        await DemoDataSeeder.ResetAsync(context, DemoDataGenerator.Generate(new Random(7), DateTimeOffset.UtcNow), CancellationToken.None);
    }

    [Fact]
    public async Task TheDemoHasOneScopedAlertRule_BesideTheDefaultPack()
    {
        await SeedDemoAsync();

        await using var context = CreateContext();
        var rules = await context.AlertRules.AsNoTracking().Include(rule => rule.Scope).ToListAsync();
        var auroraGroupId = await context.Groups.Where(group => group.Name == DemoDataSeeder.ViewerScopedGroupName).Select(group => group.Id).SingleAsync();

        Assert.Equal(AlertRuleKinds.All.Count, rules.Count(rule => rule.DefaultKey is not null));
        Assert.Equal(2, rules.Count(rule => rule.DefaultKey is null));
        var scoped = Assert.Single(rules, rule => rule.DefaultKey is null && rule.Kind == AlertTypes.MissedReport);
        Assert.Equal(AlertTypes.MissedReport, scoped.Kind);
        Assert.Equal("{\"days\":\"5\"}", scoped.ParametersJson);
        Assert.True(scoped.StopProcessing);
        var entry = Assert.Single(scoped.Scope);
        Assert.Equal((AlertRuleScopeTarget.Group, auroraGroupId, false), (entry.Target, entry.TargetId, entry.Exclude));
        var defaultMissedReport = rules.Single(rule => rule.DefaultKey == AlertTypes.MissedReport);
        Assert.True(scoped.Position < defaultMissedReport.Position, "the demo rule must sit above the default so it applies first");
    }

    [Fact]
    public async Task TheSeededDemoAlerts_BelongToTheirDefaultRules()
    {
        await SeedDemoAsync();

        await using var context = CreateContext();
        var alerts = await context.AlertEvents.AsNoTracking().ToListAsync();

        Assert.NotEmpty(alerts);
        Assert.All(alerts, alert => Assert.NotNull(alert.AlertRuleId));
    }

    [Fact]
    public async Task ResettingTheDemoTwice_LeavesTheSameRules()
    {
        await SeedDemoAsync();
        await SeedDemoAsync();

        await using var context = CreateContext();
        Assert.Equal(AlertRuleKinds.All.Count + 2, await context.AlertRules.CountAsync());
        Assert.Equal(2, await context.AlertRuleScopeEntries.CountAsync());
    }

    [Fact]
    public async Task TheDemoHasAnUnderReviewExample_ForCobaltFreight()
    {
        await SeedDemoAsync();

        await using var context = CreateContext();
        var rules = await context.AlertRules.AsNoTracking().Include(rule => rule.Scope).Where(rule => rule.Kind == AlertTypes.NewUnreviewedSender).OrderBy(rule => rule.Position).ToListAsync();
        var cobaltGroupId = await context.Groups.Where(group => group.Name == "Cobalt Freight").Select(group => group.Id).SingleAsync();

        Assert.Equal(2, rules.Count);
        var nag = rules[0];
        Assert.Null(nag.DefaultKey);
        Assert.True(nag.StopProcessing);
        var parameters = new AlertRuleParameters(nag.ParametersJson).UnderReview();
        Assert.Equal(new DotMarc.Reporting.UnderReviewHandling(DotMarc.Reporting.UnderReviewMode.AlertAgainAfterDays, 14), parameters);
        var entry = Assert.Single(nag.Scope);
        Assert.Equal((AlertRuleScopeTarget.Group, cobaltGroupId, false), (entry.Target, entry.TargetId, entry.Exclude));
        var defaultParameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(rules[1].ParametersJson!)!;
        Assert.Equal("decided", defaultParameters[AlertRuleParameterKeys.UnderReview]);
    }

    [Fact]
    public async Task EveryDemoRuleThatHasParameters_StoresThemAll()
    {
        await SeedDemoAsync();

        await using var context = CreateContext();
        foreach (var rule in await context.AlertRules.AsNoTracking().ToListAsync())
        {
            var defined = AlertRuleKinds.Find(rule.Kind)!.Parameters.Select(parameter => parameter.Key).Order().ToList();
            var stored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ParametersJson!)!.Keys.Order().ToList();
            Assert.Equal(defined, stored);
        }
    }

    [Fact]
    public async Task ResettingTheDemo_ClearsDestinations_AndEveryDemoRuleSendsToAllAndFollowsTheTicketRules()
    {
        await SeedDemoAsync();
        await using (var context = CreateContext())
        {
            var rule = await context.AlertRules.FirstAsync();
            var destination = new AlertDestination { Name = "Left over", Kind = AlertChannel.Teams, Url = "https://example.test/hook" };
            context.AlertDestinations.Add(destination);
            await context.SaveChangesAsync();
            rule.DestinationMode = AlertDestinationMode.Selected;
            rule.Destinations.Add(new AlertRuleDestination { AlertDestinationId = destination.Id });
            await context.SaveChangesAsync();
        }

        await SeedDemoAsync();

        await using var verify = CreateContext();
        Assert.Empty(await verify.AlertDestinations.ToListAsync());
        Assert.Empty(await verify.AlertRuleDestinations.ToListAsync());
        Assert.All(await verify.AlertRules.AsNoTracking().ToListAsync(),
            rule => Assert.Equal((AlertDestinationMode.All, AlertTicketMode.FollowRules), (rule.DestinationMode, rule.TicketMode)));
    }

    [Fact]
    public async Task OnlyTheAuroraDemoRuleHasQuietHours_AndNoDemoRuleEscalates()
    {
        await SeedDemoAsync();

        await using var context = CreateContext();
        var rules = await context.AlertRules.AsNoTracking().Include(rule => rule.EscalationDestinations).ToListAsync();

        var quiet = Assert.Single(rules, rule => rule.QuietMode != AlertQuietMode.Off);
        Assert.Equal("Missing report: Aurora Retail reports weekly", quiet.Name);
        Assert.Equal((AlertQuietMode.Hold, (int?)(22 * 60), (int?)(7 * 60), true), (quiet.QuietMode, quiet.QuietStartMinutes, quiet.QuietEndMinutes, quiet.QuietIgnoredForCritical));
        Assert.All(rules, rule => Assert.Null(rule.EscalateAfterMinutes));
        Assert.All(rules, rule => Assert.Empty(rule.EscalationDestinations));
    }

    [Fact]
    public async Task ResettingTheDemo_ClearsEscalationDestinations()
    {
        await SeedDemoAsync();
        await using (var context = CreateContext())
        {
            var destination = new AlertDestination { Name = "Left over", Kind = AlertChannel.Slack, Url = "https://hooks.slack.com/services/T0/B0/x" };
            context.AlertDestinations.Add(destination);
            await context.SaveChangesAsync();
            var rule = await context.AlertRules.FirstAsync();
            rule.EscalateAfterMinutes = 30;
            rule.EscalationDestinations.Add(new AlertRuleEscalationDestination { AlertDestinationId = destination.Id });
            await context.SaveChangesAsync();
        }

        await SeedDemoAsync();

        await using var verify = CreateContext();
        Assert.Empty(await verify.AlertRuleEscalationDestinations.ToListAsync());
        Assert.All(await verify.AlertRules.AsNoTracking().ToListAsync(), rule => Assert.Null(rule.EscalateAfterMinutes));
    }

    [Fact]
    public async Task EverySeededDemoAlert_CountsAsNotified_SoNoneShowsAsHeld()
    {
        await SeedDemoAsync();

        await using var context = CreateContext();
        var alerts = await context.AlertEvents.AsNoTracking().ToListAsync();

        Assert.NotEmpty(alerts);
        Assert.All(alerts, alert => Assert.Equal(alert.CreatedUtc, alert.NotifiedUtc));
    }
}
