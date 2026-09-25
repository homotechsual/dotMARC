using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class HaloPsaSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public HaloPsaSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.HaloPsaSettingsService"));

    [Fact]
    public async Task SaveAsync_UpdatesNonSecretFields_AndLeavesSecretUnconfigured_WhenNoneProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await HaloPsaSettingsService.SaveAsync(context, secretStore, new HaloPsaSettings
        {
            Enabled = true,
            AccountName = "contoso",
            AuthServerUrl = "https://contoso.halopsa.com/auth",
            ResourceServerUrl = "https://contoso.halopsa.com/api",
            ClientId = "client-id",
            TicketTypeId = 5,
            DefaultPriorityId = 2,
            ClosedStatusId = 9,
            WebhookSecret = "webhook-secret"
        }, newClientSecret: null);

        await using var verify = CreateContext();
        var saved = await HaloPsaSettingsService.GetAsync(verify);
        Assert.True(saved.Enabled);
        Assert.Equal("contoso", saved.AccountName);
        Assert.False(saved.ClientSecretConfigured);
        Assert.Null(await secretStore.GetSecretAsync(HaloPsaSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_KeepsTheNamesOfTheChosenOptions()
    {
        await using var context = CreateContext();
        await HaloPsaSettingsService.SaveAsync(context, CreateSecretStore(), new HaloPsaSettings
        {
            TicketTypeId = 23, TicketTypeName = "RMM Alert",
            DefaultPriorityId = 4, DefaultPriorityName = "Low",
            ClosedStatusId = 9, ClosedStatusName = "Closed",
            AssignedAgentId = 3, AssignedAgentName = "Mikey O'Toole"
        }, newClientSecret: null);

        await using var verify = CreateContext();
        var saved = await HaloPsaSettingsService.GetAsync(verify);
        Assert.Equal("RMM Alert", saved.TicketTypeName);
        Assert.Equal("Low", saved.DefaultPriorityName);
        Assert.Equal("Closed", saved.ClosedStatusName);
        Assert.Equal("Mikey O'Toole", saved.AssignedAgentName);
    }

    [Fact]
    public async Task SaveAsync_KeepsTheAssignedAgent_AndCanClearItBackToDontAssign()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await HaloPsaSettingsService.SaveAsync(context, secretStore, new HaloPsaSettings { AssignedAgentId = 3 }, newClientSecret: null);

        await using var afterAssign = CreateContext();
        Assert.Equal(3, (await HaloPsaSettingsService.GetAsync(afterAssign)).AssignedAgentId);

        await using var clearContext = CreateContext();
        await HaloPsaSettingsService.SaveAsync(clearContext, secretStore, new HaloPsaSettings { AssignedAgentId = null }, newClientSecret: null);

        await using var afterClear = CreateContext();
        Assert.Null((await HaloPsaSettingsService.GetAsync(afterClear)).AssignedAgentId);
    }

    [Fact]
    public async Task SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await HaloPsaSettingsService.SaveAsync(context, secretStore, new HaloPsaSettings { Enabled = true }, newClientSecret: "the-real-secret");

        await using var verify = CreateContext();
        var saved = await HaloPsaSettingsService.GetAsync(verify);
        Assert.True(saved.ClientSecretConfigured);
        Assert.Equal("the-real-secret", await secretStore.GetSecretAsync(HaloPsaSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await HaloPsaSettingsService.SaveAsync(context, secretStore, new HaloPsaSettings { Enabled = true }, newClientSecret: "first-secret");

        await using var secondContext = CreateContext();
        await HaloPsaSettingsService.SaveAsync(secondContext, secretStore, new HaloPsaSettings { Enabled = false, AccountName = "changed" }, newClientSecret: null);

        Assert.Equal("first-secret", await secretStore.GetSecretAsync(HaloPsaSettings.SecretStoreKey));

        await using var verify = CreateContext();
        var verified = await HaloPsaSettingsService.GetAsync(verify);
        Assert.True(verified.ClientSecretConfigured);
    }
}
