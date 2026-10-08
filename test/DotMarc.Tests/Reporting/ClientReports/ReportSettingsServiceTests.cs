using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ReportSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ReportSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task TheDefaults_AreUtcAt6()
    {
        await using var context = CreateContext();

        var settings = await ReportSettingsService.GetAsync(context);

        Assert.Equal(("UTC", 6), (settings.TimeZoneId, settings.SendHour));
    }

    [Fact]
    public async Task Save_StoresTheZoneAndHour_AndAudits()
    {
        await using var context = CreateContext();
        var updated = await ReportSettingsService.GetAsync(context);
        updated.TimeZoneId = "Europe/London";
        updated.SendHour = 7;

        await ReportSettingsService.SaveAsync(context, TestActors.Admin, updated);

        await using var verify = CreateContext();
        var saved = await ReportSettingsService.GetAsync(verify);
        Assert.Equal(("Europe/London", 7), (saved.TimeZoneId, saved.SendHour));
        Assert.Equal(AuditActions.ReportSettingsSaved, (await verify.AuditEntries.SingleAsync()).Action);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons", 6, "Mars/Olympus_Mons isn't a time zone this server knows.")]
    [InlineData("UTC", 24, "Send hour must be between 0 and 23.")]
    public async Task Save_RefusesAnUnknownZoneOrHour(string zone, int hour, string message)
    {
        await using var context = CreateContext();
        var updated = await ReportSettingsService.GetAsync(context);
        updated.TimeZoneId = zone;
        updated.SendHour = hour;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => ReportSettingsService.SaveAsync(context, TestActors.Admin, updated));

        Assert.StartsWith(message, exception.Message);
    }
}
