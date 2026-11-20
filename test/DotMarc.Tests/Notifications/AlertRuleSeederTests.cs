using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertRuleSeederTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertRuleSeederTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task Seeding_CreatesOneDefaultRulePerKind_WithTheKindsNameAndSeverity()
    {
        await using var context = CreateContext();

        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);

        var rules = await context.AlertRules.AsNoTracking().OrderBy(rule => rule.Position).ToListAsync();
        Assert.Equal(AlertRuleKinds.All.Select(kind => kind.Key), rules.Select(rule => rule.Kind));
        Assert.All(rules, rule =>
        {
            var kind = AlertRuleKinds.Find(rule.Kind)!;
            Assert.Equal(rule.Kind, rule.DefaultKey);
            Assert.Equal(kind.DisplayName, rule.Name);
            Assert.Equal(kind.DefaultSeverity, rule.Severity);
            Assert.Equal(kind.EnabledByDefault, rule.Enabled);
            Assert.True(rule.StopProcessing);
            Assert.NotNull(rule.ParametersJson);
        });
    }

    [Fact]
    public async Task Seeding_IsIdempotent()
    {
        await using var context = CreateContext();

        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);

        Assert.Equal(AlertRuleKinds.All.Count, await context.AlertRules.CountAsync());
    }

    [Fact]
    public async Task Seeding_DoesNotOverwriteAnAdminsChanges()
    {
        await using (var context = CreateContext())
        {
            await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
            var rule = await context.AlertRules.SingleAsync(candidate => candidate.DefaultKey == AlertTypes.MissedReport);
            rule.Name = "Renamed";
            rule.Severity = "Info";
            rule.Enabled = false;
            await context.SaveChangesAsync();
        }

        await using var seedContext = CreateContext();
        await AlertRuleSeeder.EnsureDefaultPackAsync(seedContext, CancellationToken.None);

        var kept = await seedContext.AlertRules.AsNoTracking().SingleAsync(candidate => candidate.DefaultKey == AlertTypes.MissedReport);
        Assert.Equal("Renamed", kept.Name);
        Assert.Equal("Info", kept.Severity);
        Assert.False(kept.Enabled);
    }

    [Fact]
    public async Task Seeding_AddsAMissingKindOnly()
    {
        await using (var context = CreateContext())
        {
            await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
            context.AlertRules.Remove(await context.AlertRules.SingleAsync(candidate => candidate.DefaultKey == AlertTypes.SpfRecordBroken));
            await context.SaveChangesAsync();
        }

        await using var seedContext = CreateContext();
        await AlertRuleSeeder.EnsureDefaultPackAsync(seedContext, CancellationToken.None);

        Assert.Equal(AlertRuleKinds.All.Count, await seedContext.AlertRules.CountAsync());
        Assert.Single(await seedContext.AlertRules.Where(candidate => candidate.DefaultKey == AlertTypes.SpfRecordBroken).ToListAsync());
    }

    [Fact]
    public async Task Seeding_PointsAlertsWithNoRuleAtTheirTypesDefaultRule()
    {
        int otherRuleId;
        await using (var context = CreateContext())
        {
            var otherRule = new AlertRule { Kind = AlertTypes.MissedReport, Name = "Other", Severity = "Warning" };
            context.AlertRules.Add(otherRule);
            await context.SaveChangesAsync();
            otherRuleId = otherRule.Id;
            context.AlertEvents.AddRange(
                NewAlert("a.test", AlertTypes.MissedReport, resolved: false),
                NewAlert("b.test", AlertTypes.NewUnreviewedSender, resolved: true),
                NewAlert("c.test", AlertTypes.MissedReport, resolved: false, ruleId: otherRuleId));
            await context.SaveChangesAsync();
        }

        await using (var seedContext = CreateContext())
        {
            await AlertRuleSeeder.EnsureDefaultPackAsync(seedContext, CancellationToken.None);
        }

        await using var readContext = CreateContext();
        var defaultRules = await readContext.AlertRules.AsNoTracking().Where(rule => rule.DefaultKey != null).ToDictionaryAsync(rule => rule.Kind, rule => rule.Id);
        var alerts = await readContext.AlertEvents.AsNoTracking().ToDictionaryAsync(alert => alert.DomainName);
        Assert.Equal(defaultRules[AlertTypes.MissedReport], alerts["a.test"].AlertRuleId);
        Assert.Equal(defaultRules[AlertTypes.NewUnreviewedSender], alerts["b.test"].AlertRuleId);
        Assert.Equal(otherRuleId, alerts["c.test"].AlertRuleId);
    }

    [Fact]
    public async Task TwoSeedersAtOnce_EndWithOneRulePerKind()
    {
        await using var firstContext = CreateContext();
        await using var secondContext = CreateContext();

        await Task.WhenAll(
            AlertRuleSeeder.EnsureDefaultPackAsync(firstContext, CancellationToken.None),
            AlertRuleSeeder.EnsureDefaultPackAsync(secondContext, CancellationToken.None));

        await using var readContext = CreateContext();
        Assert.Equal(AlertRuleKinds.All.Count, await readContext.AlertRules.CountAsync());
    }

    private static AlertEvent NewAlert(string domainName, string alertType, bool resolved, int? ruleId = null) => new()
    {
        DomainName = domainName, AlertType = alertType, Severity = "Warning", Title = "t", Message = "m", IsResolved = resolved, AlertRuleId = ruleId
    };
}
