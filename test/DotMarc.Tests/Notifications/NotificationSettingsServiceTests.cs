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
        Assert.Equal("Teams", settings.DeliveryMode);
        Assert.Equal(2, settings.MissingReportThresholdDays);
        Assert.Equal(180, settings.CooldownMinutes);
        Assert.Equal(300, settings.MonitorIntervalSeconds);
    }

    [Fact]
    public async Task SaveAsync_UpdatesTheSingletonRow_RatherThanInsertingASecondOne()
    {
        await using var context = CreateContext();

        await NotificationSettingsService.SaveAsync(context, new NotificationSettings
        {
            Enabled = false,
            DeliveryMode = "Generic",
            TeamsWebhookUrl = "https://example.test/teams",
            GenericWebhookUrl = "https://example.test/generic",
            MissingReportThresholdDays = 5,
            CooldownMinutes = 60,
            MonitorIntervalSeconds = 120
        }, CancellationToken.None);

        await using var verify = CreateContext();
        var stored = await verify.NotificationSettings.SingleAsync(CancellationToken.None);

        Assert.False(stored.Enabled);
        Assert.Equal("Generic", stored.DeliveryMode);
        Assert.Equal("https://example.test/teams", stored.TeamsWebhookUrl);
        Assert.Equal("https://example.test/generic", stored.GenericWebhookUrl);
        Assert.Equal(5, stored.MissingReportThresholdDays);
        Assert.Equal(60, stored.CooldownMinutes);
        Assert.Equal(120, stored.MonitorIntervalSeconds);
    }

    [Fact]
    public async Task SaveAsync_PersistsAcrossASecondSave_StillAsExactlyOneRow()
    {
        await using var context = CreateContext();

        await NotificationSettingsService.SaveAsync(context, new NotificationSettings { Enabled = false, DeliveryMode = "Teams" }, CancellationToken.None);
        await NotificationSettingsService.SaveAsync(context, new NotificationSettings { Enabled = true, DeliveryMode = "Both" }, CancellationToken.None);

        await using var verify = CreateContext();
        Assert.Single(verify.NotificationSettings);
        var stored = await verify.NotificationSettings.SingleAsync(CancellationToken.None);
        Assert.True(stored.Enabled);
        Assert.Equal("Both", stored.DeliveryMode);
    }
}
