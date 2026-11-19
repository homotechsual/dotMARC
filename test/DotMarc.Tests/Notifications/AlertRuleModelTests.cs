using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertRuleModelTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertRuleModelTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task ARule_WithScope_RoundTrips()
    {
        await using (var context = CreateContext())
        {
            context.AlertRules.Add(new AlertRule
            {
                Kind = AlertTypes.MissedReport,
                Name = "Missing report for Aurora",
                Severity = "Warning",
                Scope =
                [
                    new AlertRuleScopeEntry { Target = AlertRuleScopeTarget.Group, TargetId = 5 },
                    new AlertRuleScopeEntry { Target = AlertRuleScopeTarget.Domain, TargetId = 9, Exclude = true }
                ]
            });
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();
        var rule = await readContext.AlertRules.Include(candidate => candidate.Scope).SingleAsync();

        Assert.True(rule.Enabled);
        Assert.True(rule.StopProcessing);
        Assert.Equal(2, rule.Scope.Count);
        Assert.Contains(rule.Scope, entry => entry.Target == AlertRuleScopeTarget.Group && entry.TargetId == 5 && !entry.Exclude);
        Assert.Contains(rule.Scope, entry => entry.Target == AlertRuleScopeTarget.Domain && entry.TargetId == 9 && entry.Exclude);
    }

    [Fact]
    public async Task DefaultKey_IsUnique_ButManyRulesCanHaveNone()
    {
        await using (var context = CreateContext())
        {
            context.AlertRules.Add(new AlertRule { Kind = AlertTypes.MissedReport, Name = "A", Severity = "Warning", DefaultKey = AlertTypes.MissedReport });
            context.AlertRules.Add(new AlertRule { Kind = AlertTypes.MissedReport, Name = "B", Severity = "Warning" });
            context.AlertRules.Add(new AlertRule { Kind = AlertTypes.MissedReport, Name = "C", Severity = "Warning" });
            await context.SaveChangesAsync();
        }

        await using var duplicateContext = CreateContext();
        duplicateContext.AlertRules.Add(new AlertRule { Kind = AlertTypes.MissedReport, Name = "Again", Severity = "Warning", DefaultKey = AlertTypes.MissedReport });

        await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingARule_DeletesItsScope_AndKeepsItsAlerts()
    {
        int ruleId;
        await using (var context = CreateContext())
        {
            var rule = new AlertRule
            {
                Kind = AlertTypes.MissedReport,
                Name = "Rule",
                Severity = "Warning",
                Scope = [new AlertRuleScopeEntry { Target = AlertRuleScopeTarget.Tag, TargetId = 3 }]
            };
            context.AlertRules.Add(rule);
            await context.SaveChangesAsync();
            ruleId = rule.Id;
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "example.test", AlertType = AlertTypes.MissedReport, Severity = "Warning", Title = "t", Message = "m", AlertRuleId = ruleId
            });
            await context.SaveChangesAsync();
        }

        await using (var deleteContext = CreateContext())
        {
            deleteContext.AlertRules.Remove(await deleteContext.AlertRules.SingleAsync());
            await deleteContext.SaveChangesAsync();
        }

        await using var readContext = CreateContext();
        Assert.Empty(readContext.AlertRuleScopeEntries);
        var alert = await readContext.AlertEvents.SingleAsync();
        Assert.Null(alert.AlertRuleId);
    }
}
