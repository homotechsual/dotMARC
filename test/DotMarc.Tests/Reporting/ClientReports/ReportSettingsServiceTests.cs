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

        Assert.Equal(("UTC", 6, "en-GB"), (settings.TimeZoneId, settings.SendHour, settings.NumberFormat));
    }

    [Fact]
    public async Task Save_StoresTheZoneAndHour_AndAudits()
    {
        await using var context = CreateContext();
        var updated = await ReportSettingsService.GetAsync(context);
        updated.TimeZoneId = "Europe/London";
        updated.SendHour = 7;
        updated.NumberFormat = "de-DE";

        await ReportSettingsService.SaveAsync(context, TestActors.Admin, updated);

        await using var verify = CreateContext();
        var saved = await ReportSettingsService.GetAsync(verify);
        Assert.Equal(("Europe/London", 7, "de-DE"), (saved.TimeZoneId, saved.SendHour, saved.NumberFormat));
        Assert.Equal(AuditActions.ReportSettingsSaved, (await verify.AuditEntries.SingleAsync()).Action);
    }

    [Theory]
    [InlineData("xx-QQ", "xx-QQ isn't a number format this server knows.")]
    [InlineData("de", "de isn't a number format this server knows.")] // a language without a region has no settled separators
    public async Task Save_RefusesAnUnknownNumberFormat(string numberFormat, string message)
    {
        await using var context = CreateContext();
        var updated = await ReportSettingsService.GetAsync(context);
        updated.NumberFormat = numberFormat;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => ReportSettingsService.SaveAsync(context, TestActors.Admin, updated));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public void TheNumberFormats_AreRegionsByName()
    {
        var formats = ReportSettingsService.ListNumberFormats();

        Assert.Contains(formats, format => format.Name == "en-GB" && format.DisplayName.Contains("United Kingdom"));
        Assert.Contains(formats, format => format.Name == "de-DE");
        Assert.DoesNotContain(formats, format => format.Name == "de");
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
