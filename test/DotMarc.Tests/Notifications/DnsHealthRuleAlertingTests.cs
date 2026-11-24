using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>DNS health alerts raised by the cycle from rules: one alert per rule, and per-rule auto-close.</summary>
[Collection("Postgres")]
public sealed class DnsHealthRuleAlertingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsHealthRuleAlertingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await SaveSettingsAsync();
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

    private async Task SaveSettingsAsync(int autoCloseDays = 0)
    {
        await using var context = CreateContext();
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings
        {
            Enabled = true, TeamsEnabled = true, TeamsWebhookUrl = "https://example.test/webhook", AcknowledgeableAutoCloseDays = autoCloseDays
        });
    }

    private async Task RunCycleAsync()
    {
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();
    }

    private async Task<int> DefaultRuleIdAsync(string kind)
    {
        await using var context = CreateContext();
        return await context.AlertRules.Where(rule => rule.DefaultKey == kind).Select(rule => rule.Id).SingleAsync();
    }

    /// <summary>A monitored domain whose SPF check has been failing and whose failure is already confirmed, so the next cycle raises.</summary>
    private async Task SeedConfirmedSpfFailureAsync()
    {
        await using var context = CreateContext();
        var domain = new Domain
        {
            Name = "contoso.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30), LastReportReceivedUtc = DateTimeOffset.UtcNow,
            SpfCheckStatus = SpfCheckStatus.MissingRecord, SpfCheckedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        domain.AlertStates.Add(new DomainAlertState
        {
            Item = DnsHealthItems.Spf, HasPassed = true,
            PendingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2), RecheckDueUtc = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(15)
        });
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
    }

    private async Task<int> SeedPolicyAlertAsync(int? ruleId, double alertAgeDays)
    {
        await using var context = CreateContext();
        var domain = new Domain
        {
            Name = "policy.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30), LastReportReceivedUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            DmarcPolicy = DmarcPolicyLevel.Quarantine, DmarcSubdomainPolicy = DmarcPolicyLevel.Quarantine, DmarcPercent = 100
        };
        domain.AlertStates.Add(new DomainAlertState
        {
            Item = DnsHealthItems.DmarcPolicy, Baseline = "p=reject; sp=reject; pct=100",
            PendingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2), RecheckDueUtc = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(15)
        });
        context.Domains.Add(domain);
        var alert = new AlertEvent
        {
            DomainName = "policy.example", AlertType = AlertTypes.DmarcPolicyWeakened, Severity = "Warning", Title = "DMARC policy weakened", Message = "m",
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-alertAgeDays), AlertRuleId = ruleId
        };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return alert.Id;
    }

    [Fact]
    public async Task StackedRules_ForTheSameCheck_RaiseOneAlertEach()
    {
        await SeedConfirmedSpfFailureAsync();
        int stackedRuleId;
        await using (var context = CreateContext())
        {
            var stacked = new AlertRule { Kind = AlertTypes.SpfRecordBroken, Name = "Stacked SPF", Severity = "Info", Position = -10, StopProcessing = false };
            context.AlertRules.Add(stacked);
            await context.SaveChangesAsync();
            stackedRuleId = stacked.Id;
        }

        await RunCycleAsync();

        await using var verify = CreateContext();
        var alerts = await verify.AlertEvents.Where(alert => alert.AlertType == AlertTypes.SpfRecordBroken).ToListAsync();
        var defaultSpfRuleId = await DefaultRuleIdAsync(AlertTypes.SpfRecordBroken);
        Assert.Equal(2, alerts.Count);
        Assert.Equal("Info", alerts.Single(alert => alert.AlertRuleId == stackedRuleId).Severity);
        Assert.Equal("Warning", alerts.Single(alert => alert.AlertRuleId == defaultSpfRuleId).Severity);
    }

    [Fact]
    public async Task AutoCloseDays_OnARule_OverridesTheGlobalSetting()
    {
        var policyRuleId = await DefaultRuleIdAsync(AlertTypes.DmarcPolicyWeakened);
        var alertId = await SeedPolicyAlertAsync(policyRuleId, alertAgeDays: 2);
        await using (var context = CreateContext())
        {
            (await context.AlertRules.SingleAsync(rule => rule.Id == policyRuleId)).AutoCloseDays = 1;
            await context.SaveChangesAsync();
        }

        await RunCycleAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(alert => alert.Id == alertId)).IsResolved);
    }

    [Fact]
    public async Task WithNoRuleAutoCloseDays_TheGlobalSettingStillApplies()
    {
        await SaveSettingsAsync(autoCloseDays: 30);
        var policyRuleId = await DefaultRuleIdAsync(AlertTypes.DmarcPolicyWeakened);
        var alertId = await SeedPolicyAlertAsync(policyRuleId, alertAgeDays: 2);

        await RunCycleAsync();

        await using var verify = CreateContext();
        Assert.False((await verify.AlertEvents.SingleAsync(alert => alert.Id == alertId)).IsResolved);
    }

    private async Task SeedDomainWithNonePolicyAsync()
    {
        await using var context = CreateContext();
        var domain = new Domain
        {
            Name = "target.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30), LastReportReceivedUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            DmarcPolicy = DmarcPolicyLevel.None, DmarcSubdomainPolicy = DmarcPolicyLevel.None, DmarcPercent = 100
        };
        domain.AlertStates.Add(new DomainAlertState
        {
            Item = DnsHealthItems.DmarcTarget,
            PendingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2), RecheckDueUtc = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(15)
        });
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
    }

    private async Task SetSystemTargetAsync(DmarcPolicyLevel? policy)
    {
        await using var context = CreateContext();
        var recordSettings = await DnsRecordSettingsService.GetAsync(context);
        recordSettings.TargetDmarcPolicy = policy;
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ADomainBelowTheSystemTarget_OpensAnAlert_AndClearingTheTargetResolvesIt()
    {
        await SeedDomainWithNonePolicyAsync();
        await SetSystemTargetAsync(DmarcPolicyLevel.Reject);

        await RunCycleAsync();

        await using (var verify = CreateContext())
        {
            var alert = await verify.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.DmarcPolicyBelowTarget);
            Assert.False(alert.IsResolved);
            Assert.Equal("The DMARC policy for target.example is p=none; its target is p=reject (system-wide). Reasons: p is none, target is reject; sp is none, target is reject.", alert.Message);
        }

        await SetSystemTargetAsync(null);
        await RunCycleAsync();

        await using var afterClearing = CreateContext();
        Assert.True((await afterClearing.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.DmarcPolicyBelowTarget)).IsResolved);
    }

    [Fact]
    public async Task WithNoTargetAnywhere_TheCycleRaisesNoBelowTargetAlert()
    {
        await SeedDomainWithNonePolicyAsync();

        await RunCycleAsync();

        await using var verify = CreateContext();
        Assert.DoesNotContain(await verify.AlertEvents.ToListAsync(), alert => alert.AlertType == AlertTypes.DmarcPolicyBelowTarget);
    }
}
