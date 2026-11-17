using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting;

[Collection("Postgres")]
public sealed class DashboardPreferencesTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DashboardPreferencesTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static readonly DashboardPreferences Sample = new(
        GroupedView: true, GroupId: 4, TagId: 2, ShowFilters: true, Status: "Warning", MinPassRatePercent: 90,
        Monitored: true, Dmarc: "Misconfigured", MtaSts: "Active", Dkim: "OK");

    [Fact]
    public async Task APersonWithNothingSaved_GetsNull()
    {
        await using var context = CreateContext();

        Assert.Null(await DashboardPreferencesStore.GetAsync(context, "user-1"));
    }

    [Fact]
    public async Task SavedPreferences_ComeBackExactly_AndSavingAgainReplacesThem()
    {
        await using (var context = CreateContext())
        {
            await DashboardPreferencesStore.SaveAsync(context, "user-1", Sample);
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(Sample, await DashboardPreferencesStore.GetAsync(context, "user-1"));
            await DashboardPreferencesStore.SaveAsync(context, "user-1", new DashboardPreferences(GroupedView: false));
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(new DashboardPreferences(GroupedView: false), await DashboardPreferencesStore.GetAsync(context, "user-1"));
            Assert.Equal(1, await context.UserDashboardPreferences.CountAsync());
        }
    }

    [Fact]
    public async Task PreferencesBelongToOnePerson_AndClearingOnlyRemovesTheirs()
    {
        await using var context = CreateContext();
        await DashboardPreferencesStore.SaveAsync(context, "user-1", Sample);
        await DashboardPreferencesStore.SaveAsync(context, "user-2", new DashboardPreferences(GroupedView: true));

        await DashboardPreferencesStore.ClearAsync(context, "user-1");

        Assert.Null(await DashboardPreferencesStore.GetAsync(context, "user-1"));
        Assert.Equal(new DashboardPreferences(GroupedView: true), await DashboardPreferencesStore.GetAsync(context, "user-2"));
    }

    [Fact]
    public async Task UnreadableSavedSettings_FallBackToTheDefaults_InsteadOfBreakingTheDashboard()
    {
        await using var context = CreateContext();
        context.UserDashboardPreferences.Add(new UserDashboardPreference { UserKey = "user-1", SettingsJson = "{not json", UpdatedUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        Assert.Null(await DashboardPreferencesStore.GetAsync(context, "user-1"));
    }

    [Fact]
    public void TheUserKey_IsTheObjectId_ElseTheEmail_AndNeverAnApiKey()
    {
        Assert.Equal("oid-1", DashboardPreferencesStore.UserKeyOf(AuditActor.ForUser("oid-1", "a@contoso.com", "A")));
        Assert.Equal("a@contoso.com", DashboardPreferencesStore.UserKeyOf(AuditActor.ForUser(null, "a@contoso.com", "A")));
        Assert.Null(DashboardPreferencesStore.UserKeyOf(AuditActor.ForUser(null, null, "A")));
        Assert.Null(DashboardPreferencesStore.UserKeyOf(AuditActor.ForApiKey(3, "k", "admin")));
    }
}
