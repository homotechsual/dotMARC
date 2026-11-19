using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Demo;

/// <summary>The demo shows alert rules: the default pack, plus one rule scoped to the Aurora Retail group.</summary>
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
        var scoped = Assert.Single(rules, rule => rule.DefaultKey is null);
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
        Assert.Equal(AlertRuleKinds.All.Count + 1, await context.AlertRules.CountAsync());
        Assert.Equal(1, await context.AlertRuleScopeEntries.CountAsync());
    }
}
