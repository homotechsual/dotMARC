using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa.ConnectWise;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.ConnectWise;

[Collection("Postgres")]
public sealed class ConnectWiseSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ConnectWiseSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static ConnectWiseSettings Complete(Action<ConnectWiseSettings>? change = null)
    {
        var settings = new ConnectWiseSettings
        {
            Enabled = true, SiteUrl = "api-eu.myconnectwise.net", CompanyId = "contoso", PublicKey = "public",
            BoardId = 1, BoardName = "Help Desk", StatusId = 16, PriorityId = 8, ClosedStatusId = 20,
        };
        change?.Invoke(settings);
        return settings;
    }

    [Fact]
    public async Task Save_StoresThePrivateKeyInTheSecretStore_AndAuditsItAsASecret()
    {
        await using var context = CreateContext();
        var secrets = new FakeSecretStore();

        await ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), " private ");

        Assert.Equal("private", secrets.Secrets[ConnectWiseSettings.PrivateKeySecretKey]);
        await using var verify = CreateContext();
        var saved = await ConnectWiseSettingsService.GetAsync(verify);
        Assert.True(saved.PrivateKeyConfigured);
        Assert.Equal("Help Desk", saved.BoardName);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.ConnectWiseSettingsSaved, entry.Action);
        Assert.Contains(entry.Changes, change => change.Field == "Private key" && change.Secret);
        Assert.DoesNotContain(entry.Changes, change => (change.New ?? "").Contains("private"));
    }

    [Theory]
    [InlineData("https://api-eu.myconnectwise.net/", "api-eu.myconnectwise.net")]
    [InlineData("  api-na.myconnectwise.net  ", "api-na.myconnectwise.net")]
    [InlineData("http://cw.contoso.com/v4_6_release/apis/3.0", "cw.contoso.com")]
    public async Task Save_KeepsOnlyTheSiteHost(string entered, string saved)
    {
        await using var context = CreateContext();

        await ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.SiteUrl = entered), "private");

        await using var verify = CreateContext();
        Assert.Equal(saved, (await ConnectWiseSettingsService.GetAsync(verify)).SiteUrl);
    }

    [Fact]
    public async Task Save_ABlankClientIdOverride_FallsBackTodotMARCsOwn()
    {
        await using var context = CreateContext();

        await ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.ClientIdOverride = "   "), "private");

        await using var verify = CreateContext();
        var saved = await ConnectWiseSettingsService.GetAsync(verify);
        Assert.Null(saved.ClientIdOverride);
        Assert.Equal(ConnectWiseSettings.DefaultClientId, saved.EffectiveClientId);
    }

    [Fact]
    public async Task Save_AnOverride_WinsOverdotMARCsOwnClientId()
    {
        await using var context = CreateContext();

        await ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.ClientIdOverride = " mine "), "private");

        await using var verify = CreateContext();
        Assert.Equal("mine", (await ConnectWiseSettingsService.GetAsync(verify)).EffectiveClientId);
    }

    [Fact]
    public async Task Save_NothingChanged_RecordsNothing()
    {
        await using (var context = CreateContext())
        {
            await ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(), "private");
        }

        await using (var context = CreateContext())
        {
            await ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(), null);
        }

        await using var verify = CreateContext();
        Assert.Single(await verify.AuditEntries.ToListAsync());
    }
}
