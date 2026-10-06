using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa.Autotask;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.Autotask;

[Collection("Postgres")]
public sealed class AutotaskSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AutotaskSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static AutotaskSettings Complete(Action<AutotaskSettings>? change = null)
    {
        var settings = new AutotaskSettings
        {
            Enabled = true, Username = "api@contoso.com", IntegrationCodeOverride = "TRACKING",
            QueueId = 29683354, QueueName = "Monitoring", TicketTypeId = 1, PriorityId = 2,
        };
        change?.Invoke(settings);
        return settings;
    }

    [Fact]
    public async Task ANewInstall_DefaultsTheClosedStatusToComplete()
    {
        await using var context = CreateContext();

        var settings = await AutotaskSettingsService.GetAsync(context);

        Assert.Equal((AutotaskSettings.CompleteStatus, "Complete"), (settings.ClosedStatusId, settings.ClosedStatusName));
    }

    [Fact]
    public async Task Save_StoresTheSecretInTheSecretStore_AndAuditsItAsASecret()
    {
        await using var context = CreateContext();
        var secrets = new FakeSecretStore();

        await AutotaskSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), " s3cret ");

        Assert.Equal("s3cret", secrets.Secrets[AutotaskSettings.SecretStoreKey]);
        await using var verify = CreateContext();
        var saved = await AutotaskSettingsService.GetAsync(verify);
        Assert.True(saved.SecretConfigured);
        Assert.Equal("Monitoring", saved.QueueName);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.AutotaskSettingsSaved, entry.Action);
        Assert.Contains(entry.Changes, change => change.Field == "Secret" && change.Secret);
        Assert.DoesNotContain(entry.Changes, change => (change.New ?? "").Contains("s3cret"));
    }

    [Fact]
    public async Task Save_TurnedOnWithNoTrackingIdentifier_IsRefused_WhileThereIsNoBuiltInOne()
    {
        if (AutotaskSettings.DefaultIntegrationCode.Length > 0)
        {
            return; // dotMARC has its own tracking identifier, so none is needed.
        }

        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            AutotaskSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.IntegrationCodeOverride = null), "s3cret"));

        Assert.Contains("API tracking identifier", exception.Message);
    }

    [Fact]
    public async Task Save_TrimsTheUsername()
    {
        await using var context = CreateContext();

        await AutotaskSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.Username = "  api@contoso.com "), "s3cret");

        await using var verify = CreateContext();
        Assert.Equal("api@contoso.com", (await AutotaskSettingsService.GetAsync(verify)).Username);
    }
}
