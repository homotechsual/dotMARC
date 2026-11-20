using System.Text.Json;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The default pack carries its own values: created from the settings on a new install, and copied once from them into the
/// rules a Phase 1 install already has. After that the rules decide, and the retired settings are never read.</summary>
[Collection("Postgres")]
public sealed class AlertRuleMaterialisationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertRuleMaterialisationTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    /// <summary>Changes the settings row directly: the Alert settings page stops offering these fields later in this phase, so a
    /// test that needs old values writes them as an upgraded database would hold them.</summary>
    private async Task SetSettingsAsync(Action<NotificationSettings> change)
    {
        await using var context = CreateContext();
        var settings = await context.NotificationSettings.SingleAsync();
        change(settings);
        await context.SaveChangesAsync();
    }

    private async Task<Dictionary<string, AlertRule>> DefaultRulesAsync()
    {
        await using var context = CreateContext();
        return await context.AlertRules.AsNoTracking().Where(rule => rule.DefaultKey != null).ToDictionaryAsync(rule => rule.Kind);
    }

    private static Dictionary<string, string> Parameters(AlertRule rule) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ParametersJson!)!;

    private async Task EnsureAsync()
    {
        await using var context = CreateContext();
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task ANewInstall_CreatesDefaultRulesWithExplicitDefaultValues()
    {
        await EnsureAsync();

        var rules = await DefaultRulesAsync();
        foreach (var kind in AlertRuleKinds.All)
        {
            var rule = rules[kind.Key];
            Assert.NotNull(rule.ParametersJson);
            var stored = Parameters(rule);
            Assert.Equal(kind.Parameters.Select(parameter => parameter.Key).Order(), stored.Keys.Order());
            Assert.All(kind.Parameters, parameter => Assert.Equal(parameter.Default, stored[parameter.Key]));
            Assert.Equal(kind.EnabledByDefault, rule.Enabled);
            Assert.Null(rule.AutoCloseDays);
        }
    }

    [Fact]
    public async Task CustomisedSettings_AreCopiedIntoTheRulesOnCreation()
    {
        await SetSettingsAsync(settings =>
        {
            settings.MissingReportThresholdDays = 5;
            settings.SuspiciousRejectMinVolume = 33;
            settings.SuspiciousRejectNonBenignPercent = 70;
            settings.NewSenderDays = 3;
            settings.NewSenderMinVolume = 44;
            settings.UnauthorisedDeliveredMinVolume = 55;
            settings.SpfAlertMode = DnsHealthAlertMode.WheneverItFails;
            settings.MxAlertMode = DnsHealthAlertMode.Off;
            settings.NewSenderAlertEnabled = false;
            settings.UnauthorisedDeliveredAlertEnabled = false;
            settings.DmarcPolicyWeakenedEnabled = false;
            settings.AcknowledgeableAutoCloseDays = 9;
        });

        await EnsureAsync();

        var rules = await DefaultRulesAsync();
        Assert.Equal("5", Parameters(rules[AlertTypes.MissedReport])[AlertRuleParameterKeys.MissingDays]);
        Assert.Equal(("33", "70"), (Parameters(rules[AlertTypes.SuspiciousRejectActivity])[AlertRuleParameterKeys.MinimumMessages], Parameters(rules[AlertTypes.SuspiciousRejectActivity])[AlertRuleParameterKeys.NonBenignPercent]));
        var newSender = rules[AlertTypes.NewUnreviewedSender];
        Assert.Equal(("3", "44", false), (Parameters(newSender)[AlertRuleParameterKeys.NewDays], Parameters(newSender)[AlertRuleParameterKeys.MinimumMessages], newSender.Enabled));
        Assert.Equal(("55", false), (Parameters(rules[AlertTypes.UnauthorisedSenderDelivered])[AlertRuleParameterKeys.MinimumMessages], rules[AlertTypes.UnauthorisedSenderDelivered].Enabled));
        Assert.Equal("WheneverItFails", Parameters(rules[AlertTypes.SpfRecordBroken])[AlertRuleParameterKeys.Mode]);
        // Off becomes a switched off rule, with the ordinary default mode kept for when it is switched on.
        Assert.Equal((false, "WhenItBreaks"), (rules[AlertTypes.MxRecordBroken].Enabled, Parameters(rules[AlertTypes.MxRecordBroken])[AlertRuleParameterKeys.Mode]));
        Assert.Equal((false, 9), (rules[AlertTypes.DmarcPolicyWeakened].Enabled, rules[AlertTypes.DmarcPolicyWeakened].AutoCloseDays));
        Assert.Equal((true, 9), (rules[AlertTypes.NameserversChanged].Enabled, rules[AlertTypes.NameserversChanged].AutoCloseDays));
    }

    [Fact]
    public async Task ZeroAutoCloseDays_IsStoredAsNoAutoClose()
    {
        await SetSettingsAsync(settings => settings.AcknowledgeableAutoCloseDays = 0);

        await EnsureAsync();

        Assert.Null((await DefaultRulesAsync())[AlertTypes.DmarcPolicyWeakened].AutoCloseDays);
    }

    [Fact]
    public async Task APhaseOneInstall_GetsItsValuesCopiedOnce()
    {
        // What Phase 1 left behind: default rules with no parameters of their own.
        await using (var context = CreateContext())
        {
            var phaseOnePack = AlertRuleSeeder.DefaultPack();
            Assert.All(phaseOnePack, rule => rule.ParametersJson = null);
            context.AlertRules.AddRange(phaseOnePack);
            await context.SaveChangesAsync();
        }

        await SetSettingsAsync(settings =>
        {
            settings.MissingReportThresholdDays = 6;
            settings.NewSenderAlertEnabled = false;
            settings.NameserversChangedEnabled = true;
            settings.AcknowledgeableAutoCloseDays = 4;
        });

        await EnsureAsync();

        var rules = await DefaultRulesAsync();
        Assert.Equal("6", Parameters(rules[AlertTypes.MissedReport])[AlertRuleParameterKeys.MissingDays]);
        Assert.False(rules[AlertTypes.NewUnreviewedSender].Enabled);
        Assert.Equal(4, rules[AlertTypes.NameserversChanged].AutoCloseDays);
        Assert.All(rules.Values, rule => Assert.NotNull(rule.ParametersJson));
    }

    [Fact]
    public async Task ALaterEditToARule_IsNeverOverwrittenByTheSettings()
    {
        await EnsureAsync();
        await using (var context = CreateContext())
        {
            var rule = await context.AlertRules.SingleAsync(candidate => candidate.DefaultKey == AlertTypes.MissedReport);
            rule.ParametersJson = "{\"days\":\"9\"}";
            await context.SaveChangesAsync();
        }

        await SetSettingsAsync(settings => settings.MissingReportThresholdDays = 4);
        await EnsureAsync();
        await EnsureAsync();

        Assert.Equal("9", Parameters((await DefaultRulesAsync())[AlertTypes.MissedReport])[AlertRuleParameterKeys.MissingDays]);
    }

    [Fact]
    public async Task ADisabledPhaseOneRule_StaysDisabled_WhenItsSettingIsOn()
    {
        await using (var context = CreateContext())
        {
            var phaseOnePack = AlertRuleSeeder.DefaultPack();
            Assert.All(phaseOnePack, rule => rule.ParametersJson = null);
            phaseOnePack.Single(rule => rule.Kind == AlertTypes.NewUnreviewedSender).Enabled = false;
            context.AlertRules.AddRange(phaseOnePack);
            await context.SaveChangesAsync();
        }

        await SetSettingsAsync(settings => settings.NewSenderAlertEnabled = true);
        await EnsureAsync();

        Assert.False((await DefaultRulesAsync())[AlertTypes.NewUnreviewedSender].Enabled);
    }

    [Fact]
    public async Task TheEngine_NoLongerReadsTheRetiredSettings()
    {
        await EnsureAsync();
        await SetSettingsAsync(settings =>
        {
            settings.Enabled = true;
            settings.TeamsEnabled = true;
            settings.TeamsWebhookUrl = "https://example.test/webhook";
            settings.MissingReportThresholdDays = 30;
        });
        await using (var context = CreateContext())
        {
            context.Domains.Add(new Domain { Name = "stale.test", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30), LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-3) });
            await context.SaveChangesAsync();
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();

        // The rule says 2 days. The setting says 30, which would have kept this quiet before.
        await using var verify = CreateContext();
        Assert.Single(await verify.AlertEvents.Where(alert => alert.AlertType == AlertTypes.MissedReport).ToListAsync());
    }
}
