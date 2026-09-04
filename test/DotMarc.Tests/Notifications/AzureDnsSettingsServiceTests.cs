using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AzureDnsSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AzureDnsSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DatabaseSecretStore CreateSecretStore() =>
        new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.AzureDnsSettingsService"));

    [Fact]
    public async Task SaveAsync_UpdatesTenantIdAndClientId_AndLeavesSecretUnconfigured_WhenNoneProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await AzureDnsSettingsService.SaveAsync(context, secretStore, new AzureDnsSettings { TenantId = "tenant-id", ClientId = "client-id" }, newClientSecret: null);

        await using var verify = CreateContext();
        var saved = await AzureDnsSettingsService.GetAsync(verify);
        Assert.Equal("tenant-id", saved.TenantId);
        Assert.Equal("client-id", saved.ClientId);
        Assert.False(saved.ClientSecretConfigured);
        Assert.Null(await secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await AzureDnsSettingsService.SaveAsync(context, secretStore, new AzureDnsSettings { TenantId = "tenant-id", ClientId = "client-id" }, newClientSecret: "the-real-secret");

        await using var verify = CreateContext();
        var saved = await AzureDnsSettingsService.GetAsync(verify);
        Assert.True(saved.ClientSecretConfigured);
        Assert.Equal("the-real-secret", await secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await AzureDnsSettingsService.SaveAsync(context, secretStore, new AzureDnsSettings { TenantId = "tenant-id", ClientId = "client-id" }, newClientSecret: "first-secret");

        await using var secondContext = CreateContext();
        await AzureDnsSettingsService.SaveAsync(secondContext, secretStore, new AzureDnsSettings { TenantId = "tenant-id", ClientId = "changed" }, newClientSecret: null);

        Assert.Equal("first-secret", await secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey));

        await using var verify = CreateContext();
        var verified = await AzureDnsSettingsService.GetAsync(verify);
        Assert.True(verified.ClientSecretConfigured);
        Assert.Equal("changed", verified.ClientId);
    }
}
