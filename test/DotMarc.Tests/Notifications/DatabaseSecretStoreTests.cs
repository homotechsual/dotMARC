using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class DatabaseSecretStoreTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DatabaseSecretStoreTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        DataProtectionProvider.Create("DotMarc.Tests.EncryptedSecret");

    [Fact]
    public async Task SetThenGet_RoundTripsTheSecret_UnderItsKey()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        await store.SetSecretAsync("Test.Key", "super-secret-value");
        var result = await store.GetSecretAsync("Test.Key");

        Assert.Equal("super-secret-value", result);
    }

    [Fact]
    public async Task DifferentKeys_StoreIndependentValues()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        await store.SetSecretAsync("Test.KeyA", "value-a");
        await store.SetSecretAsync("Test.KeyB", "value-b");

        Assert.Equal("value-a", await store.GetSecretAsync("Test.KeyA"));
        Assert.Equal("value-b", await store.GetSecretAsync("Test.KeyB"));
    }

    [Fact]
    public async Task SetSecretAsync_OverwritesAnExistingValueUnderTheSameKey()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());
        await store.SetSecretAsync("Test.Key", "first-value");

        await store.SetSecretAsync("Test.Key", "second-value");

        Assert.Equal("second-value", await store.GetSecretAsync("Test.Key"));
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNull_WhenTheKeyWasNeverSet()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        Assert.Null(await store.GetSecretAsync("Test.NeverSet"));
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNull_WhenProtectedWithADifferentKeyRing()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());
        await store.SetSecretAsync("Test.Key", "super-secret-value");

        var storeWithADifferentKeyRing = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.SomeOtherApp"));

        Assert.Null(await storeWithADifferentKeyRing.GetSecretAsync("Test.Key"));
    }
}
