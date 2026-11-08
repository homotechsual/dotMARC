using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa.SuperOps;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.SuperOps;

[Collection("Postgres")]
public sealed class SuperOpsSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SuperOpsSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static SuperOpsSettings Complete(Action<SuperOpsSettings>? change = null)
    {
        var settings = new SuperOpsSettings
        {
            Enabled = true, Region = SuperOpsRegions.Us, Subdomain = "contoso", RequestType = "Incident", NewStatus = "Open", ClosedStatus = "Resolved",
        };
        change?.Invoke(settings);
        return settings;
    }

    [Fact]
    public async Task TheSeededRow_HasSensibleDefaults()
    {
        await using var context = CreateContext();

        var seeded = await SuperOpsSettingsService.GetAsync(context);

        Assert.False(seeded.Enabled);
        Assert.Equal(SuperOpsRegions.Us, seeded.Region);
        Assert.Equal("Incident", seeded.RequestType);
        Assert.Equal("Open", seeded.NewStatus);
        Assert.Equal("Resolved", seeded.ClosedStatus);
    }

    [Fact]
    public async Task Save_StoresTheTokenInTheSecretStore_AndAuditsItAsASecret()
    {
        await using var context = CreateContext();
        var secrets = new FakeSecretStore();

        await SuperOpsSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), " token ");

        Assert.Equal("token", secrets.Secrets[SuperOpsSettings.ApiTokenSecretKey]);
        await using var verify = CreateContext();
        Assert.True((await SuperOpsSettingsService.GetAsync(verify)).ApiTokenConfigured);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.SuperOpsSettingsSaved, entry.Action);
        Assert.Contains(entry.Changes, change => change.Field == "API token" && change.Secret);
        Assert.DoesNotContain(entry.Changes, change => (change.New ?? "").Contains("token"));
    }

    [Fact]
    public async Task Save_WithNoNewToken_KeepsTheSavedOne()
    {
        await using var context = CreateContext();
        var secrets = new FakeSecretStore();
        await SuperOpsSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), "token");

        await using var again = CreateContext();
        await SuperOpsSettingsService.SaveAsync(again, TestActors.Admin, secrets, Complete(settings => settings.Priority = "High"), null);

        Assert.Equal("token", secrets.Secrets[SuperOpsSettings.ApiTokenSecretKey]);
        await using var verify = CreateContext();
        var saved = await SuperOpsSettingsService.GetAsync(verify);
        Assert.True(saved.ApiTokenConfigured);
        Assert.Equal("High", saved.Priority);
    }

    [Theory]
    [InlineData("contoso", "contoso")]
    [InlineData("  contoso  ", "contoso")]
    [InlineData("contoso.superops.ai", "contoso")]
    [InlineData("https://contoso.superops.ai/#/tickets", "contoso")]
    public async Task Save_KeepsJustTheSubdomain(string typed, string expected)
    {
        await using var context = CreateContext();

        await SuperOpsSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.Subdomain = typed), null);

        await using var verify = CreateContext();
        Assert.Equal(expected, (await SuperOpsSettingsService.GetAsync(verify)).Subdomain);
    }

    [Fact]
    public async Task Save_TrimsChoices_AndTurnsBlankOptionalOnesIntoNull()
    {
        await using var context = CreateContext();

        await SuperOpsSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(),
            Complete(settings => (settings.RequestType, settings.Priority, settings.Urgency, settings.Category) = (" Service Request ", "  ", "", " Email ")), null);

        await using var verify = CreateContext();
        var saved = await SuperOpsSettingsService.GetAsync(verify);
        Assert.Equal("Service Request", saved.RequestType);
        Assert.Null(saved.Priority);
        Assert.Null(saved.Urgency);
        Assert.Equal("Email", saved.Category);
    }

    [Fact]
    public async Task Save_RefusesAnUnknownRegion_AndSavesNothing()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SuperOpsSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.Region = "mars"), null));

        await using var verify = CreateContext();
        Assert.False((await SuperOpsSettingsService.GetAsync(verify)).Enabled);
    }

    [Fact]
    public async Task Save_WithNothingChanged_WritesNoAuditEntry()
    {
        await using var context = CreateContext();
        var unchanged = await SuperOpsSettingsService.GetAsync(context);

        await SuperOpsSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), unchanged, null);

        await using var verify = CreateContext();
        Assert.Empty(await verify.AuditEntries.ToListAsync());
    }
}
