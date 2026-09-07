using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class GoogleCloudDnsSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public GoogleCloudDnsSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.GoogleCloudDnsSettingsService"));

    [Fact]
    public async Task SaveAsync_UpdatesClientId_AndLeavesSecretUnconfigured_WhenNoneProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await GoogleCloudDnsSettingsService.SaveAsync(context, secretStore, new GoogleCloudDnsSettings { ClientId = "client-id" }, newClientSecret: null);

        await using var verify = CreateContext();
        var saved = await GoogleCloudDnsSettingsService.GetAsync(verify);
        Assert.Equal("client-id", saved.ClientId);
        Assert.False(saved.ClientSecretConfigured);
        Assert.Null(await secretStore.GetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await GoogleCloudDnsSettingsService.SaveAsync(context, secretStore, new GoogleCloudDnsSettings { ClientId = "client-id" }, newClientSecret: "the-real-secret");

        await using var verify = CreateContext();
        var saved = await GoogleCloudDnsSettingsService.GetAsync(verify);
        Assert.True(saved.ClientSecretConfigured);
        Assert.Equal("the-real-secret", await secretStore.GetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await GoogleCloudDnsSettingsService.SaveAsync(context, secretStore, new GoogleCloudDnsSettings { ClientId = "client-id" }, newClientSecret: "first-secret");

        await using var secondContext = CreateContext();
        await GoogleCloudDnsSettingsService.SaveAsync(secondContext, secretStore, new GoogleCloudDnsSettings { ClientId = "changed" }, newClientSecret: null);

        Assert.Equal("first-secret", await secretStore.GetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey));

        await using var verify = CreateContext();
        var verified = await GoogleCloudDnsSettingsService.GetAsync(verify);
        Assert.True(verified.ClientSecretConfigured);
        Assert.Equal("changed", verified.ClientId);
    }
}
