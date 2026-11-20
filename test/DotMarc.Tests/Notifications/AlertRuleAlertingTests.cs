using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The alerting cycle as rules drive it: ordering, Stop processing, scope, stacked rules and what happens to an alert
/// whose rule stops applying. The default pack alone is covered by the older alerting tests, which must keep passing.</summary>
[Collection("Postgres")]
public sealed class AlertRuleAlertingTests : IAsyncLifetime
{
    private const string MailchimpIp = "198.51.100.7";

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertRuleAlertingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings
        {
            Enabled = true, TeamsEnabled = true, TeamsWebhookUrl = "https://example.test/webhook", MissingReportThresholdDays = 2, CooldownMinutes = 180
        });
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

    private async Task<(int DomainId, int GroupId)> SeedDomainAsync(string name, double lastReportDaysAgo, string? groupName = null)
    {
        await using var context = CreateContext();
        var group = groupName is null ? null : await context.Groups.FirstOrDefaultAsync(candidate => candidate.Name == groupName) ?? new Group { Name = groupName };
        var domain = new Domain
        {
            Name = name, IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-90),
            LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-lastReportDaysAgo), Groups = group is null ? [] : [group]
        };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return (domain.Id, group?.Id ?? 0);
    }

    private async Task SetLastReportAsync(int domainId, double daysAgo)
    {
        await using var context = CreateContext();
        var domain = await context.Domains.SingleAsync(candidate => candidate.Id == domainId);
        domain.LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        await context.SaveChangesAsync();
    }

    private async Task<int> AddRuleAsync(string kind, int position, bool stop = true, string? parametersJson = null, int? cooldownMinutes = null, params AlertRuleScopeEntry[] scope)
    {
        await using var context = CreateContext();
        var rule = new AlertRule
        {
            Kind = kind, Name = $"Rule at {position}", Severity = "Warning", Position = position, StopProcessing = stop,
            ParametersJson = parametersJson, CooldownMinutes = cooldownMinutes, Scope = [.. scope]
        };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();
        return rule.Id;
    }

    private async Task<int> DefaultRuleIdAsync(string kind)
    {
        await using var context = CreateContext();
        return await context.AlertRules.Where(rule => rule.DefaultKey == kind).Select(rule => rule.Id).SingleAsync();
    }

    private async Task UpdateRuleAsync(int ruleId, Action<AlertRule> change)
    {
        await using var context = CreateContext();
        var rule = await context.AlertRules.Include(candidate => candidate.Scope).SingleAsync(candidate => candidate.Id == ruleId);
        change(rule);
        await context.SaveChangesAsync();
    }

    private async Task RunCycleAsync()
    {
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();
    }

    private async Task<List<AlertEvent>> AlertsAsync(string alertType, string? domainName = null)
    {
        await using var context = CreateContext();
        return await context.AlertEvents
            .Where(alert => alert.AlertType == alertType && (domainName == null || alert.DomainName == domainName))
            .OrderBy(alert => alert.Id)
            .ToListAsync();
    }

    private static AlertRuleScopeEntry InGroup(int groupId) => new() { Target = AlertRuleScopeTarget.Group, TargetId = groupId };

    [Fact]
    public async Task ARuleScopedToAGroup_WithAHigherThreshold_ReplacesTheDefaultForThatGroupOnly()
    {
        var (_, auroraGroupId) = await SeedDomainAsync("aurora.test", lastReportDaysAgo: 3, groupName: "Aurora");
        await SeedDomainAsync("other.test", lastReportDaysAgo: 3);
        await AddRuleAsync(AlertTypes.MissedReport, position: -10, parametersJson: "{\"days\":\"5\"}", scope: InGroup(auroraGroupId));

        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.MissedReport));
        Assert.Equal("other.test", alert.DomainName);
        Assert.Equal(await DefaultRuleIdAsync(AlertTypes.MissedReport), alert.AlertRuleId);
    }

    [Fact]
    public async Task ARuleThatDoesNotStop_RaisesItsOwnAlert_BesideTheDefault_EachWithItsOwnCooldown()
    {
        await SeedDomainAsync("stacked.test", lastReportDaysAgo: 3);
        var stackedRuleId = await AddRuleAsync(AlertTypes.MissedReport, position: -10, stop: false, parametersJson: "{\"days\":\"1\"}");

        await RunCycleAsync();
        await RunCycleAsync();

        var alerts = await AlertsAsync(AlertTypes.MissedReport);
        Assert.Equal(2, alerts.Count);
        Assert.Equal(
            new[] { stackedRuleId, await DefaultRuleIdAsync(AlertTypes.MissedReport) }.Order(),
            alerts.Select(alert => alert.AlertRuleId!.Value).Order());
    }

    [Fact]
    public async Task ResolvingOneStackedRule_LeavesTheOther()
    {
        var (domainId, _) = await SeedDomainAsync("stacked.test", lastReportDaysAgo: 3);
        var stackedRuleId = await AddRuleAsync(AlertTypes.MissedReport, position: -10, stop: false, parametersJson: "{\"days\":\"1\"}");
        await RunCycleAsync();

        // 1.5 days: still stale for the 1 day rule, fresh for the default 2 day rule.
        await SetLastReportAsync(domainId, 1.5);
        await RunCycleAsync();

        var alerts = await AlertsAsync(AlertTypes.MissedReport);
        Assert.Equal(2, alerts.Count);
        Assert.False(alerts.Single(alert => alert.AlertRuleId == stackedRuleId).IsResolved);
        Assert.True(alerts.Single(alert => alert.AlertRuleId != stackedRuleId).IsResolved);
    }

    [Fact]
    public async Task DisablingARule_ResolvesItsOpenAlerts_AndLeavesTheOthers()
    {
        await SeedDomainAsync("stacked.test", lastReportDaysAgo: 3);
        var stackedRuleId = await AddRuleAsync(AlertTypes.MissedReport, position: -10, stop: false, parametersJson: "{\"days\":\"1\"}");
        await RunCycleAsync();

        await UpdateRuleAsync(stackedRuleId, rule => rule.Enabled = false);
        await RunCycleAsync();

        var alerts = await AlertsAsync(AlertTypes.MissedReport);
        Assert.True(alerts.Single(alert => alert.AlertRuleId == stackedRuleId).IsResolved);
        Assert.False(alerts.Single(alert => alert.AlertRuleId != stackedRuleId).IsResolved);
    }

    [Fact]
    public async Task RescopingARuleAwayFromADomain_ResolvesThatDomainsAlert()
    {
        var (_, auroraGroupId) = await SeedDomainAsync("aurora.test", lastReportDaysAgo: 3, groupName: "Aurora");
        var ruleId = await AddRuleAsync(AlertTypes.MissedReport, position: -10, stop: false, parametersJson: "{\"days\":\"1\"}", scope: InGroup(auroraGroupId));
        await RunCycleAsync();

        await UpdateRuleAsync(ruleId, rule =>
        {
            rule.Scope.Clear();
            rule.Scope.Add(InGroup(auroraGroupId + 100));
        });
        await RunCycleAsync();

        var alerts = await AlertsAsync(AlertTypes.MissedReport, "aurora.test");
        Assert.True(alerts.Single(alert => alert.AlertRuleId == ruleId).IsResolved);
        Assert.False(alerts.Single(alert => alert.AlertRuleId != ruleId).IsResolved);
    }

    [Fact]
    public async Task APutAboveStoppingRule_ResolvesTheShadowedRulesAlert()
    {
        await SeedDomainAsync("shadowed.test", lastReportDaysAgo: 3);
        await RunCycleAsync();
        var defaultAlert = Assert.Single(await AlertsAsync(AlertTypes.MissedReport));
        Assert.False(defaultAlert.IsResolved);

        // A rule above the default, applying to everything, stopping, and happy with a 10 day gap: the default no longer applies.
        var shadowingRuleId = await AddRuleAsync(AlertTypes.MissedReport, position: -10, parametersJson: "{\"days\":\"10\"}");
        await RunCycleAsync();

        var alerts = await AlertsAsync(AlertTypes.MissedReport);
        Assert.Single(alerts);
        Assert.True(alerts[0].IsResolved);
        Assert.NotEqual(shadowingRuleId, alerts[0].AlertRuleId);
    }

    [Fact]
    public async Task ACooldownOverride_OnARule_IsHonoured()
    {
        await SeedDomainAsync("cooldown.test", lastReportDaysAgo: 3);
        var ruleId = await AddRuleAsync(AlertTypes.MissedReport, position: -10, cooldownMinutes: 0);

        await RunCycleAsync();
        await RunCycleAsync();

        var alerts = await AlertsAsync(AlertTypes.MissedReport);
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, alert => Assert.Equal(ruleId, alert.AlertRuleId));
    }

    [Fact]
    public async Task EveryRuleOfAKindDisabled_RaisesNothing_AndResolvesOpenAlerts()
    {
        await SeedDomainAsync("quiet.test", lastReportDaysAgo: 3);
        await RunCycleAsync();
        Assert.Single(await AlertsAsync(AlertTypes.MissedReport));

        await UpdateRuleAsync(await DefaultRuleIdAsync(AlertTypes.MissedReport), rule => rule.Enabled = false);
        await RunCycleAsync();
        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.MissedReport));
        Assert.True(alert.IsResolved);
    }

    [Fact]
    public async Task AnUpgradedOpenAlert_WithNoRule_IsAdoptedByTheDefaultRule_AndNotDuplicated()
    {
        await SeedDomainAsync("upgraded.test", lastReportDaysAgo: 3);
        await using (var context = CreateContext())
        {
            // The default pack exists from InitializeAsync, so point the alert back at no rule, as an upgrade leaves it.
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "upgraded.test", AlertType = AlertTypes.MissedReport, Severity = "Warning", Title = "Missing expected DMARC report", Message = "m",
                CreatedUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
            await context.AlertRules.Where(rule => rule.DefaultKey == AlertTypes.MissedReport).ExecuteDeleteAsync();
        }

        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.MissedReport));
        Assert.False(alert.IsResolved);
        Assert.Equal(await DefaultRuleIdAsync(AlertTypes.MissedReport), alert.AlertRuleId);
    }

    [Fact]
    public async Task ADisabledDefaultRule_DoesNotSilenceAStackedRuleOfTheSameKind()
    {
        var (domainId, _) = await SeedDomainAsync("senders.test", lastReportDaysAgo: 0);
        await AddReportAsync(domainId, daysAgo: 1, Mail("senders.test", MailchimpIp, 30));
        await UpdateRuleAsync(await DefaultRuleIdAsync(AlertTypes.NewUnreviewedSender), rule => rule.Enabled = false);
        var stackedRuleId = await AddRuleAsync(AlertTypes.NewUnreviewedSender, position: -10, stop: false);

        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender));
        Assert.Equal(stackedRuleId, alert.AlertRuleId);
    }

    [Fact]
    public async Task ANewSenderRule_WithItsOwnNewDays_UsesIt()
    {
        var (domainId, _) = await SeedDomainAsync("senders.test", lastReportDaysAgo: 0);
        await AddReportAsync(domainId, daysAgo: 3, Mail("senders.test", MailchimpIp, 30));
        var shortWindowRuleId = await AddRuleAsync(AlertTypes.NewUnreviewedSender, position: -10, stop: false, parametersJson: "{\"newDays\":\"1\"}");

        await RunCycleAsync();

        // First seen 3 days ago: new for the default rule (7 days), not for the 1 day rule.
        var alert = Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender));
        Assert.NotEqual(shortWindowRuleId, alert.AlertRuleId);
        Assert.Equal(await DefaultRuleIdAsync(AlertTypes.NewUnreviewedSender), alert.AlertRuleId);
    }

    private static ReportRecord Mail(string domainName, string ip, int count)
    {
        var record = new ReportRecord
        {
            SourceIp = ip, MessageCount = count, Disposition = DispositionResult.None, HeaderFrom = domainName,
            SpfResult = AuthResult.Pass, DkimResult = AuthResult.Fail
        };
        record.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "bounce.mcsv.net", Result = DmarcMechanismResult.Pass });
        return record;
    }

    private async Task AddReportAsync(int domainId, double daysAgo, params ReportRecord[] records)
    {
        await using var context = CreateContext();
        var received = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        context.Reports.Add(new Report
        {
            DomainId = domainId, ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>",
            DateRangeBeginUtc = received.AddDays(-1), DateRangeEndUtc = received, ReceivedUtc = received, AuthDetailBackfilledUtc = received, Records = [.. records]
        });
        await context.SaveChangesAsync();
    }
}
