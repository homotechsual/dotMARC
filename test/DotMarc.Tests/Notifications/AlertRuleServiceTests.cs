using System.Text.Json;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertRuleServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertRuleServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
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
        string name = "Aurora weekly", string severity = "Warning", bool enabled = true, bool stop = true, int? cooldown = null, int? autoClose = null,
        Dictionary<string, string>? parameters = null, params AlertRuleScopeInput[] scope) =>
        new(name, enabled, severity, stop, cooldown, autoClose, parameters ?? [], scope);

    private async Task<AlertRule> DefaultRuleAsync(string kind)
    {
        await using var context = CreateContext();
        return await context.AlertRules.AsNoTracking().Include(rule => rule.Scope).SingleAsync(rule => rule.DefaultKey == kind);
    }

    private async Task<AlertRule> CreateRuleAsync(string kind, AlertRuleInput input)
    {
        await using var context = CreateContext();
        return await AlertRuleService.CreateAsync(context, TestActors.Admin, kind, input);
    }

    private async Task<List<AuditEntry>> AuditAsync(string? action = null)
    {
        await using var context = CreateContext();
        return await context.AuditEntries.AsNoTracking().Where(entry => action == null || entry.Action == action).OrderBy(entry => entry.Id).ToListAsync();
    }

    private async Task<List<AlertRule>> RulesOfAsync(string kind)
    {
        await using var context = CreateContext();
        return await context.AlertRules.AsNoTracking().Include(rule => rule.Scope).Where(rule => rule.Kind == kind).OrderBy(rule => rule.Position).ThenBy(rule => rule.Id).ToListAsync();
    }

    private static Dictionary<string, string> ParametersOf(AlertRule rule) => JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ParametersJson!)!;

    // ---- Create ----

    [Fact]
    public async Task Create_PutsTheRuleFirstInItsKind_AndAuditsIt()
    {
        var created = await CreateRuleAsync(AlertTypes.MissedReport, Input(parameters: new() { [AlertRuleParameterKeys.MissingDays] = "5" }));

        var rules = await RulesOfAsync(AlertTypes.MissedReport);
        Assert.Equal(created.Id, rules[0].Id);
        Assert.Equal("Aurora weekly", rules[0].Name);
        Assert.Null(rules[0].DefaultKey);
        var entry = Assert.Single(await AuditAsync(AuditActions.AlertRuleCreated));
        Assert.Equal(("AlertRule", created.Id.ToString(), "Aurora weekly"), (entry.TargetType, entry.TargetId, entry.TargetName));
        Assert.Equal("admin@example.com", entry.ActorEmail);
        Assert.Contains("Aurora weekly", entry.Summary);
    }

    [Fact]
    public async Task Create_StoresEveryDefinedParameter_Explicitly_WithDefaultsForTheOnesGiven()
    {
        var created = await CreateRuleAsync(AlertTypes.NewUnreviewedSender, Input(parameters: new() { [AlertRuleParameterKeys.NewDays] = "3" }));

        var stored = ParametersOf((await RulesOfAsync(AlertTypes.NewUnreviewedSender)).Single(rule => rule.Id == created.Id));
        Assert.Equal(
            new Dictionary<string, string>
            {
                [AlertRuleParameterKeys.NewDays] = "3", [AlertRuleParameterKeys.MinimumMessages] = "20",
                [AlertRuleParameterKeys.UnderReview] = "decided", [AlertRuleParameterKeys.UnderReviewDays] = "14"
            },
            stored);
    }

    [Fact]
    public async Task Create_StoresItsScope()
    {
        var created = await CreateRuleAsync(AlertTypes.MissedReport,
            Input(scope: [new AlertRuleScopeInput(AlertRuleScopeTarget.Group, 7, false), new AlertRuleScopeInput(AlertRuleScopeTarget.Domain, 9, true)]));

        var rule = (await RulesOfAsync(AlertTypes.MissedReport)).Single(candidate => candidate.Id == created.Id);
        Assert.Equal(2, rule.Scope.Count);
        Assert.Contains(rule.Scope, entry => entry is { Target: AlertRuleScopeTarget.Group, TargetId: 7, Exclude: false });
        Assert.Contains(rule.Scope, entry => entry is { Target: AlertRuleScopeTarget.Domain, TargetId: 9, Exclude: true });
    }

    [Theory]
    [InlineData(AlertTypes.MissedReport, "", "Warning", "2")]
    [InlineData(AlertTypes.MissedReport, "A rule", "Bogus", "2")]
    [InlineData(AlertTypes.MissedReport, "A rule", "Warning", "0")]
    [InlineData("NoSuchKind", "A rule", "Warning", "2")]
    public async Task Create_RefusesAnInvalidRule_AndStoresNothing(string kind, string name, string severity, string days)
    {
        await using var context = CreateContext();
        var rulesBefore = await context.AlertRules.CountAsync();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, kind, Input(name, severity, parameters: new() { [AlertRuleParameterKeys.MissingDays] = days })));

        Assert.NotEmpty(exception.Problems);
        await using var verify = CreateContext();
        Assert.Equal(rulesBefore, await verify.AlertRules.CountAsync());
        Assert.Empty(await AuditAsync(AuditActions.AlertRuleCreated));
    }

    [Fact]
    public async Task Create_RefusesAScopeOnASystemKind()
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.ApiKeyExpiring, Input(scope: new AlertRuleScopeInput(AlertRuleScopeTarget.Group, 1, false))));

        Assert.Contains(exception.Problems, problem => problem.Contains("scope"));
    }

    [Fact]
    public async Task Create_RefusesADuplicateName_WithinAKind_ButNotInAnother()
    {
        await CreateRuleAsync(AlertTypes.MissedReport, Input(name: "Same name"));
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.MissedReport, Input(name: "same NAME")));
        var other = await AlertRuleService.CreateAsync(context, TestActors.Admin, AlertTypes.TlsrptFailure, Input(name: "Same name"));

        Assert.Contains(exception.Problems, problem => problem.Contains("already"));
        Assert.True(other.Id > 0);
    }

    // ---- Update ----

    [Fact]
    public async Task Update_ChangesTheFields_AuditingEachOldAndNewValue()
    {
        var created = await CreateRuleAsync(AlertTypes.MissedReport, Input(parameters: new() { [AlertRuleParameterKeys.MissingDays] = "5" }));
        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, created.Id,
                Input(name: "Renamed", severity: "Critical", enabled: false, stop: false, cooldown: 30, parameters: new() { [AlertRuleParameterKeys.MissingDays] = "7" },
                    scope: new AlertRuleScopeInput(AlertRuleScopeTarget.Tag, 4, false)));
        }

        var rule = (await RulesOfAsync(AlertTypes.MissedReport)).Single(candidate => candidate.Id == created.Id);
        Assert.Equal(("Renamed", "Critical", false, false, 30), (rule.Name, rule.Severity, rule.Enabled, rule.StopProcessing, rule.CooldownMinutes));
        Assert.Equal("7", ParametersOf(rule)[AlertRuleParameterKeys.MissingDays]);
        Assert.Single(rule.Scope);
        var entry = Assert.Single(await AuditAsync(AuditActions.AlertRuleChanged));
        Assert.Contains(entry.Changes, change => change is { Field: "Name", Old: "Aurora weekly", New: "Renamed" });
        Assert.Contains(entry.Changes, change => change is { Field: "Severity", Old: "Warning", New: "Critical" });
        Assert.Contains(entry.Changes, change => change.Field == "Enabled");
        Assert.Contains(entry.Changes, change => change.Field == "Stop processing");
        Assert.Contains(entry.Changes, change => change is { Field: "Cooldown (minutes)", New: "30" });
        Assert.Contains(entry.Changes, change => change is { Field: "Days without a report", Old: "5", New: "7" });
        Assert.Contains(entry.Changes, change => change.Field == "Scope");
    }

    [Fact]
    public async Task Update_WithNothingChanged_WritesNoAuditEntry()
    {
        var created = await CreateRuleAsync(AlertTypes.MissedReport, Input());
        var rule = (await RulesOfAsync(AlertTypes.MissedReport)).Single(candidate => candidate.Id == created.Id);
        var same = Input(name: rule.Name, parameters: ParametersOf(rule));

        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, created.Id, same);
        }

        Assert.Empty(await AuditAsync(AuditActions.AlertRuleChanged));
    }

    [Fact]
    public async Task Update_CannotChangeTheKindOrDefaultKey_OfADefaultRule()
    {
        var defaultRule = await DefaultRuleAsync(AlertTypes.MissedReport);

        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, defaultRule.Id, Input(name: "Renamed default", parameters: new() { [AlertRuleParameterKeys.MissingDays] = "4" }));
        }

        var after = await DefaultRuleAsync(AlertTypes.MissedReport);
        Assert.Equal((AlertTypes.MissedReport, AlertTypes.MissedReport, "Renamed default"), (after.Kind, after.DefaultKey, after.Name));
    }

    [Fact]
    public async Task Update_RefusesAnOutOfRangeParameter_NamingIt_AndChangesNothing()
    {
        var defaultRule = await DefaultRuleAsync(AlertTypes.MissedReport);
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() =>
            AlertRuleService.UpdateAsync(context, TestActors.Admin, defaultRule.Id, Input(name: "Whatever", parameters: new() { [AlertRuleParameterKeys.MissingDays] = "999" })));

        Assert.Contains(exception.Problems, problem => problem.Contains("Days without a report"));
        var after = await DefaultRuleAsync(AlertTypes.MissedReport);
        Assert.Equal(defaultRule.Name, after.Name);
        Assert.Equal(defaultRule.ParametersJson, after.ParametersJson);
        Assert.Empty(await AuditAsync(AuditActions.AlertRuleChanged));
    }

    [Fact]
    public async Task Update_OfARuleThatDoesNotExist_Throws()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<AlertRuleValidationException>(() => AlertRuleService.UpdateAsync(context, TestActors.Admin, 999999, Input()));
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_RemovesACustomRule_AndItsScope_AndKeepsItsAlerts()
    {
        var created = await CreateRuleAsync(AlertTypes.MissedReport, Input(scope: new AlertRuleScopeInput(AlertRuleScopeTarget.Group, 7, false)));
        await using (var context = CreateContext())
        {
            context.AlertEvents.Add(new AlertEvent { DomainName = "x.test", AlertType = AlertTypes.MissedReport, Severity = "Warning", Title = "t", Message = "m", AlertRuleId = created.Id });
            await context.SaveChangesAsync();
            await AlertRuleService.DeleteAsync(context, TestActors.Admin, created.Id);
        }

        await using var verify = CreateContext();
        Assert.DoesNotContain(await verify.AlertRules.ToListAsync(), rule => rule.Id == created.Id);
        Assert.Equal(0, await verify.AlertRuleScopeEntries.CountAsync());
        Assert.Null((await verify.AlertEvents.SingleAsync()).AlertRuleId);
        var entry = Assert.Single(await AuditAsync(AuditActions.AlertRuleDeleted));
        Assert.Equal("Aurora weekly", entry.TargetName);
    }

    [Fact]
    public async Task Delete_RefusesADefaultRule()
    {
        var defaultRule = await DefaultRuleAsync(AlertTypes.MissedReport);
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() => AlertRuleService.DeleteAsync(context, TestActors.Admin, defaultRule.Id));

        Assert.Contains(exception.Problems, problem => problem.Contains("default"));
        Assert.NotNull(await DefaultRuleAsync(AlertTypes.MissedReport));
    }

    // ---- Move ----

    private async Task<(AlertRule First, AlertRule Second, AlertRule Third)> ThreeRulesAsync()
    {
        // Create puts each new rule first, so the order is third, second, first, then the default.
        var first = await CreateRuleAsync(AlertTypes.MissedReport, Input(name: "First"));
        var second = await CreateRuleAsync(AlertTypes.MissedReport, Input(name: "Second"));
        var third = await CreateRuleAsync(AlertTypes.MissedReport, Input(name: "Third"));
        return (first, second, third);
    }

    private async Task<List<string>> OrderOfMissedReportAsync() => (await RulesOfAsync(AlertTypes.MissedReport)).Select(rule => rule.Name).ToList();

    [Fact]
    public async Task Move_SwapsWithTheNeighbour()
    {
        var (_, second, _) = await ThreeRulesAsync();
        Assert.Equal(["Third", "Second", "First", "Missing DMARC report"], await OrderOfMissedReportAsync());

        await using (var context = CreateContext())
        {
            await AlertRuleService.MoveAsync(context, TestActors.Admin, second.Id, direction: -1);
        }

        Assert.Equal(["Second", "Third", "First", "Missing DMARC report"], await OrderOfMissedReportAsync());
        Assert.Single(await AuditAsync(AuditActions.AlertRuleMoved));

        await using (var context = CreateContext())
        {
            await AlertRuleService.MoveAsync(context, TestActors.Admin, second.Id, direction: 1);
        }

        Assert.Equal(["Third", "Second", "First", "Missing DMARC report"], await OrderOfMissedReportAsync());
    }

    [Fact]
    public async Task Move_AtTheEnds_DoesNothing_AndWritesNoAuditEntry()
    {
        var (_, _, third) = await ThreeRulesAsync();
        var last = await DefaultRuleAsync(AlertTypes.MissedReport);
        await using (var context = CreateContext())
        {
            await AlertRuleService.MoveAsync(context, TestActors.Admin, third.Id, direction: -1);
            await AlertRuleService.MoveAsync(context, TestActors.Admin, last.Id, direction: 1);
        }

        Assert.Equal(["Third", "Second", "First", "Missing DMARC report"], await OrderOfMissedReportAsync());
        Assert.Empty(await AuditAsync(AuditActions.AlertRuleMoved));
    }

    [Fact]
    public async Task Move_NeverTouchesAnotherKind()
    {
        var (_, second, _) = await ThreeRulesAsync();
        var otherBefore = (await RulesOfAsync(AlertTypes.TlsrptFailure)).Select(rule => (rule.Id, rule.Position)).ToList();

        await using (var context = CreateContext())
        {
            await AlertRuleService.MoveAsync(context, TestActors.Admin, second.Id, direction: -1);
        }

        Assert.Equal(otherBefore, (await RulesOfAsync(AlertTypes.TlsrptFailure)).Select(rule => (rule.Id, rule.Position)).ToList());
    }

    [Fact]
    public async Task Move_FixesRulesThatShareAPosition()
    {
        var (first, second, third) = await ThreeRulesAsync();
        await using (var context = CreateContext())
        {
            foreach (var rule in await context.AlertRules.Where(rule => rule.Id == first.Id || rule.Id == second.Id || rule.Id == third.Id).ToListAsync())
            {
                rule.Position = -50;
            }

            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await AlertRuleService.MoveAsync(context, TestActors.Admin, third.Id, direction: 1);
        }

        var positions = (await RulesOfAsync(AlertTypes.MissedReport)).Select(rule => rule.Position).ToList();
        Assert.Equal(positions.Distinct().Count(), positions.Count);
    }

    // ---- Reset ----

    [Fact]
    public async Task Reset_RestoresADefaultRulesShippedValues()
    {
        var defaultRule = await DefaultRuleAsync(AlertTypes.NewUnreviewedSender);
        await using (var context = CreateContext())
        {
            await AlertRuleService.UpdateAsync(context, TestActors.Admin, defaultRule.Id,
                Input(name: "Mine", severity: "Critical", enabled: false, stop: false, cooldown: 5,
                    parameters: new() { [AlertRuleParameterKeys.NewDays] = "2", [AlertRuleParameterKeys.UnderReview] = "unreviewed" },
                    scope: new AlertRuleScopeInput(AlertRuleScopeTarget.Group, 3, false)));
            await AlertRuleService.ResetToDefaultAsync(context, TestActors.Admin, defaultRule.Id);
        }

        var after = await DefaultRuleAsync(AlertTypes.NewUnreviewedSender);
        Assert.Equal(("New sender to review", "Warning", true, true, (int?)null, (int?)null), (after.Name, after.Severity, after.Enabled, after.StopProcessing, after.CooldownMinutes, after.AutoCloseDays));
        Assert.Empty(after.Scope);
        Assert.Equal("7", ParametersOf(after)[AlertRuleParameterKeys.NewDays]);
        Assert.Equal("decided", ParametersOf(after)[AlertRuleParameterKeys.UnderReview]);
        Assert.Equal(defaultRule.Position, after.Position);
        Assert.Single(await AuditAsync(AuditActions.AlertRuleReset));
    }

    [Fact]
    public async Task Reset_RefusesACustomRule()
    {
        var created = await CreateRuleAsync(AlertTypes.MissedReport, Input());
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertRuleValidationException>(() => AlertRuleService.ResetToDefaultAsync(context, TestActors.Admin, created.Id));

        Assert.Contains(exception.Problems, problem => problem.Contains("default"));
    }

    // ---- Dead scope ----

    [Fact]
    public async Task ListDeadScope_FindsEntriesForGoneTargets_WithoutChangingAnything_AndPruneRemovesThemAndAuditsIt()
    {
        int liveGroupId;
        await using (var context = CreateContext())
        {
            var group = new Group { Name = "Live group" };
            context.Groups.Add(group);
            await context.SaveChangesAsync();
            liveGroupId = group.Id;
        }

        var created = await CreateRuleAsync(AlertTypes.MissedReport, Input(scope:
        [
            new AlertRuleScopeInput(AlertRuleScopeTarget.Group, liveGroupId, false),
            new AlertRuleScopeInput(AlertRuleScopeTarget.Group, 987654, false),
            new AlertRuleScopeInput(AlertRuleScopeTarget.Tag, 987655, true),
            new AlertRuleScopeInput(AlertRuleScopeTarget.Domain, 987656, false)
        ]));

        await using (var context = CreateContext())
        {
            var dead = await AlertRuleService.ListDeadScopeAsync(context);
            Assert.Equal(3, dead.Count);
            Assert.DoesNotContain(dead, entry => entry.TargetId == liveGroupId);
            Assert.Equal(4, await context.AlertRuleScopeEntries.CountAsync());

            var removed = await AlertRuleService.PruneDeadScopeAsync(context, TestActors.Admin);
            Assert.Equal(3, removed);
        }

        var rule = (await RulesOfAsync(AlertTypes.MissedReport)).Single(candidate => candidate.Id == created.Id);
        Assert.Equal(liveGroupId, Assert.Single(rule.Scope).TargetId);
        var entry = Assert.Single(await AuditAsync(AuditActions.AlertRuleScopePruned));
        Assert.Equal("Aurora weekly", entry.TargetName);
    }

    [Fact]
    public async Task PruneDeadScope_WithNothingDead_ReturnsZero_AndWritesNoAuditEntry()
    {
        await using var context = CreateContext();

        Assert.Equal(0, await AlertRuleService.PruneDeadScopeAsync(context, TestActors.Admin));
        Assert.Empty(await AuditAsync(AuditActions.AlertRuleScopePruned));
    }
}
