using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class CloudflareDnsSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public CloudflareDnsSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.CloudflareDnsSettingsService"));

    [Fact]
    public async Task SaveAsync_UpdatesClientId_AndLeavesSecretUnconfigured_WhenNoneProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await CloudflareDnsSettingsService.SaveAsync(context, secretStore, new CloudflareDnsSettings { ClientId = "client-id" }, newClientSecret: null);

        await using var verify = CreateContext();
        var saved = await CloudflareDnsSettingsService.GetAsync(verify);
        Assert.Equal("client-id", saved.ClientId);
        Assert.False(saved.ClientSecretConfigured);
        Assert.Null(await secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await CloudflareDnsSettingsService.SaveAsync(context, secretStore, new CloudflareDnsSettings { ClientId = "client-id" }, newClientSecret: "the-real-secret");

        await using var verify = CreateContext();
        var saved = await CloudflareDnsSettingsService.GetAsync(verify);
        Assert.True(saved.ClientSecretConfigured);
        Assert.Equal("the-real-secret", await secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await CloudflareDnsSettingsService.SaveAsync(context, secretStore, new CloudflareDnsSettings { ClientId = "client-id" }, newClientSecret: "first-secret");

        await using var secondContext = CreateContext();
        await CloudflareDnsSettingsService.SaveAsync(secondContext, secretStore, new CloudflareDnsSettings { ClientId = "changed" }, newClientSecret: null);

        Assert.Equal("first-secret", await secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey));

        await using var verify = CreateContext();
        var verified = await CloudflareDnsSettingsService.GetAsync(verify);
        Assert.True(verified.ClientSecretConfigured);
        Assert.Equal("changed", verified.ClientId);
    }
}
