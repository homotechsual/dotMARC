using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class NotificationSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NotificationSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task GetAsync_ReturnsTheMigrationSeededRow_WithoutAnyExplicitSetup()
    {
        await using var context = CreateContext();

        var settings = await NotificationSettingsService.GetAsync(context, CancellationToken.None);

        Assert.True(settings.Enabled);
        Assert.True(settings.TeamsEnabled);
        Assert.False(settings.SlackEnabled);
        Assert.False(settings.GenericWebhookEnabled);
        Assert.Equal(2, settings.MissingReportThresholdDays);
        Assert.Equal(180, settings.CooldownMinutes);
        Assert.Equal(300, settings.MonitorIntervalSeconds);
    }

    [Fact]
    public async Task SaveAsync_UpdatesTheSingletonRow_RatherThanInsertingASecondOne()
    {
        await using var context = CreateContext();

        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings
        {
            Enabled = false,
            CooldownMinutes = 60,
            MonitorIntervalSeconds = 120
        }, CancellationToken.None);

        await using var verify = CreateContext();
        var stored = await verify.NotificationSettings.SingleAsync(CancellationToken.None);

        Assert.False(stored.Enabled);
        Assert.Equal(60, stored.CooldownMinutes);
        Assert.Equal(120, stored.MonitorIntervalSeconds);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheRetiredChannelFieldsAlone_BecauseDestinationsReplacedThem()
    {
        await using var context = CreateContext();

        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings
        {
            Enabled = true,
            TeamsEnabled = false,
            SlackEnabled = true,
            SlackWebhookUrl = "https://hooks.slack.com/services/T000/B000/secret",
            GenericWebhookEnabled = true,
            TeamsWebhookUrl = "https://example.test/teams",
            GenericWebhookUrl = "https://example.test/generic",
            CooldownMinutes = 61
        }, CancellationToken.None);

        await using var verify = CreateContext();
        var stored = await verify.NotificationSettings.SingleAsync(CancellationToken.None);
        Assert.Equal(61, stored.CooldownMinutes);
        Assert.True(stored.TeamsEnabled);
        Assert.False(stored.SlackEnabled);
        Assert.False(stored.GenericWebhookEnabled);
        Assert.Null(stored.TeamsWebhookUrl);
        Assert.Null(stored.SlackWebhookUrl);
        Assert.Null(stored.GenericWebhookUrl);
    }

    [Fact]
    public async Task SaveAsync_PersistsAcrossASecondSave_StillAsExactlyOneRow()
    {
        await using var context = CreateContext();

        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings { Enabled = false, TeamsEnabled = true }, CancellationToken.None);
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings { Enabled = true }, CancellationToken.None);

        await using var verify = CreateContext();
        Assert.Single(verify.NotificationSettings);
        var stored = await verify.NotificationSettings.SingleAsync(CancellationToken.None);
        Assert.True(stored.Enabled);
    }

    [Fact]
    public async Task SaveAsync_RecordsChangedSettings()
    {
        NotificationSettings updated;
        await using (var loadContext = CreateContext())
        {
            updated = await loadContext.NotificationSettings.AsNoTracking().SingleAsync();
        }
        var originalCooldown = updated.CooldownMinutes;
        updated.CooldownMinutes = originalCooldown + 30;

        await using (var context = CreateContext())
        {
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, updated);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.NotificationSettingsSaved, entry.Action);
        Assert.Contains(new AuditFieldChange("Cooldown (minutes)", originalCooldown.ToString(), (originalCooldown + 30).ToString()), entry.Changes);
        Assert.DoesNotContain(entry.Changes, change => change.Field.Contains("webhook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveAsync_RecordsNothing_WhenNothingChanged()
    {
        await using (var context = CreateContext())
        {
            var unchanged = await context.NotificationSettings.SingleAsync();
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, unchanged);
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.AuditEntries);
    }

    [Fact]
    public async Task AFreshDatabase_HasTheDnsHealthAlertDefaults()
    {
        await using var context = CreateContext();

        var settings = await NotificationSettingsService.GetAsync(context);

        Assert.All(
            [settings.DmarcAlertMode, settings.DmarcAuthorizationAlertMode, settings.TlsrptAlertMode, settings.SpfAlertMode,
             settings.MxAlertMode, settings.DkimAlertMode, settings.MtaStsAlertMode],
            mode => Assert.Equal(DnsHealthAlertMode.WhenItBreaks, mode));
        Assert.True(settings.DmarcPolicyWeakenedEnabled);
        Assert.True(settings.NameserversChangedEnabled);
        Assert.Equal(0, settings.AcknowledgeableAutoCloseDays);
    }

    [Fact]
    public async Task SavingSettings_NoLongerChangesTheRetiredFields_OrComplainsAboutThem()
    {
        NotificationSettings before;
        await using (var loadContext = CreateContext())
        {
            before = await loadContext.NotificationSettings.AsNoTracking().SingleAsync();
        }

        var updated = await CopyAsync(before);
        updated.MissingReportThresholdDays = 9;
        updated.SuspiciousRejectMinVolume = 77;
        updated.SuspiciousRejectNonBenignPercent = 12;
        updated.SpfAlertMode = DnsHealthAlertMode.WheneverItFails;
        updated.DkimAlertMode = DnsHealthAlertMode.Off;
        updated.DmarcPolicyWeakenedEnabled = false;
        updated.NameserversChangedEnabled = false;
        updated.AcknowledgeableAutoCloseDays = 5000;
        updated.NewSenderAlertEnabled = false;
        updated.NewSenderMinVolume = 0;
        updated.NewSenderDays = 99;
        updated.UnauthorisedDeliveredAlertEnabled = false;
        updated.UnauthorisedDeliveredMinVolume = -3;

        await using (var context = CreateContext())
        {
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, updated);
        }

        await using var verify = CreateContext();
        var saved = await NotificationSettingsService.GetAsync(verify);
        Assert.Equal(
            (before.MissingReportThresholdDays, before.SuspiciousRejectMinVolume, before.SuspiciousRejectNonBenignPercent, before.SpfAlertMode, before.DkimAlertMode,
                before.DmarcPolicyWeakenedEnabled, before.NameserversChangedEnabled, before.AcknowledgeableAutoCloseDays, before.NewSenderAlertEnabled,
                before.NewSenderMinVolume, before.NewSenderDays, before.UnauthorisedDeliveredAlertEnabled, before.UnauthorisedDeliveredMinVolume),
            (saved.MissingReportThresholdDays, saved.SuspiciousRejectMinVolume, saved.SuspiciousRejectNonBenignPercent, saved.SpfAlertMode, saved.DkimAlertMode,
                saved.DmarcPolicyWeakenedEnabled, saved.NameserversChangedEnabled, saved.AcknowledgeableAutoCloseDays, saved.NewSenderAlertEnabled,
                saved.NewSenderMinVolume, saved.NewSenderDays, saved.UnauthorisedDeliveredAlertEnabled, saved.UnauthorisedDeliveredMinVolume));
        Assert.Empty(verify.AuditEntries);
    }

    [Fact]
    public async Task SavingSettings_AuditsTheFieldsItKeeps_AndNotTheRetiredChannels()
    {
        NotificationSettings updated;
        await using (var loadContext = CreateContext())
        {
            updated = await CopyAsync(await loadContext.NotificationSettings.AsNoTracking().SingleAsync());
        }

        updated.MonitorIntervalSeconds += 60;
        updated.SlackEnabled = !updated.SlackEnabled;
        updated.MissingReportThresholdDays = 9;

        await using (var context = CreateContext())
        {
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, updated);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Contains(entry.Changes, change => change.Field == "Monitor interval (seconds)");
        Assert.DoesNotContain(entry.Changes, change => change.Field == "Slack");
        Assert.DoesNotContain(entry.Changes, change => change.Field.Contains("Missing report"));
    }

    private static Task<NotificationSettings> CopyAsync(NotificationSettings source) => Task.FromResult(new NotificationSettings
    {
        Id = source.Id, Enabled = source.Enabled, TeamsEnabled = source.TeamsEnabled, TeamsWebhookUrl = source.TeamsWebhookUrl,
        SlackEnabled = source.SlackEnabled, SlackWebhookUrl = source.SlackWebhookUrl, GenericWebhookEnabled = source.GenericWebhookEnabled,
        GenericWebhookUrl = source.GenericWebhookUrl, CooldownMinutes = source.CooldownMinutes, MonitorIntervalSeconds = source.MonitorIntervalSeconds,
    });
}
