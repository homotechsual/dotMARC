using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class DatabaseHaloSecretStoreTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DatabaseHaloSecretStoreTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static IDataProtectionProvider CreateProtectionProvider() =>
        DataProtectionProvider.Create("DotMarc.Tests.HaloPsa");

    [Fact]
    public async Task SetThenGet_RoundTripsTheSecret()
    {
        var store = new DatabaseHaloSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        await store.SetClientSecretAsync("super-secret-value");
        var result = await store.GetClientSecretAsync();

        Assert.Equal("super-secret-value", result);
    }

    [Fact]
    public async Task GetClientSecretAsync_ReturnsNull_WhenNeverSet()
    {
        var store = new DatabaseHaloSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        Assert.Null(await store.GetClientSecretAsync());
    }

    [Fact]
    public async Task GetClientSecretAsync_ReturnsNull_WhenProtectedWithADifferentKeyRing()
    {
        var store = new DatabaseHaloSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());
        await store.SetClientSecretAsync("super-secret-value");

        var storeWithADifferentKeyRing = new DatabaseHaloSecretStore(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.SomeOtherApp"));

        Assert.Null(await storeWithADifferentKeyRing.GetClientSecretAsync());
    }
}
