using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task SaveAsync_RecordsTheOldAndNewPeriods()
    {
        await using (var context = CreateContext())
        {
            await AuditSettingsService.SaveAsync(context, TestActors.Admin, new AuditSettings { ChangeRetentionDays = null, SignInRetentionDays = 365, PageViewRetentionDays = 30 });
        }

        await using var verify = CreateContext();
        var settings = await verify.AuditSettings.SingleAsync();
        Assert.Equal((null, 365, 30), (settings.ChangeRetentionDays, settings.SignInRetentionDays, settings.PageViewRetentionDays));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.AuditSettingsSaved, entry.Action);
        Assert.Equal(
            [new AuditFieldChange("Keep changes for", "365 days", "Keep forever"), new AuditFieldChange("Keep page views for", "90 days", "30 days")],
            entry.Changes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task SaveAsync_RefusesAPeriodOutOfRange_AndChangesNothing(int days)
    {
        await using (var context = CreateContext())
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                AuditSettingsService.SaveAsync(context, TestActors.Admin, new AuditSettings { ChangeRetentionDays = days, SignInRetentionDays = 365, PageViewRetentionDays = 90 }));
        }

        await using var verify = CreateContext();
        Assert.Equal(365, (await verify.AuditSettings.SingleAsync()).ChangeRetentionDays);
        Assert.Empty(verify.AuditEntries);
    }
}