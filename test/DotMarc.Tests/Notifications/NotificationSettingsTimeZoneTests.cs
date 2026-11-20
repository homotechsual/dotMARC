using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The time zone quiet hours are read in is a notification setting, validated and audited.</summary>
[Collection("Postgres")]
public sealed class NotificationSettingsTimeZoneTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NotificationSettingsTimeZoneTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<NotificationSettings> LoadAsync()
    {
        await using var context = CreateContext();
        return await context.NotificationSettings.AsNoTracking().SingleAsync();
    }

    [Fact]
    public async Task AValidTimeZone_IsSavedAndAudited()
    {
        var settings = await LoadAsync();
        settings.TimeZoneId = "Europe/London";

        await using (var context = CreateContext())
        {
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
        }

        Assert.Equal("Europe/London", (await LoadAsync()).TimeZoneId);
        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Contains(entry.Changes, change => change is { Field: "Time zone", Old: "UTC", New: "Europe/London" });
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnInvalidTimeZone_IsRefused_AndNothingIsSaved(string timeZoneId)
    {
        var settings = await LoadAsync();
        settings.TimeZoneId = timeZoneId;
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings));

        Assert.Contains("time zone", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UTC", (await LoadAsync()).TimeZoneId);
    }
}
