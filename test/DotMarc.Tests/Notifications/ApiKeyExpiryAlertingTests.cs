using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class ApiKeyExpiryAlertingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ApiKeyExpiryAlertingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
        settings.Enabled = true;
        settings.TeamsWebhookUrl = "https://example.test/webhook";
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
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

    private AlertingService CreateService(FakeAlertWebhookClient notifier) =>
        new(new FakeDbContextFactory(_connectionString), notifier, PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

    private async Task<ApiKey> SeedKeyAsync(string name, DateTimeOffset expiresUtc)
    {
        await using var context = CreateContext();
        var apiKey = new ApiKey
        {
            Name = name, Prefix = "dmk_" + name[..Math.Min(8, name.Length)], Hash = Guid.NewGuid().ToString("N"),
            Role = new Role { Name = $"role for {name}", Permissions = [Permission.DomainsView] },
            CreatedBy = "Test Admin", CreatedUtc = DateTimeOffset.UtcNow.AddDays(-80), ExpiresUtc = expiresUtc,
        };
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync();
        return apiKey;
    }

    [Fact]
    public async Task AKeyExpiringWithin14Days_RaisesOneAlert_AndALaterOneDoesnt()
    {
        var soon = await SeedKeyAsync("soon", DateTimeOffset.UtcNow.AddDays(10));
        await SeedKeyAsync("later", DateTimeOffset.UtcNow.AddDays(30));
        var notifier = new FakeAlertWebhookClient();

        await CreateService(notifier).CheckPinnedDomainsAsync();

        await using var context = CreateContext();
        var alert = await context.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.ApiKeyExpiring);
        Assert.Equal(AlertingService.ApiKeyAlertSubject(soon), alert.DomainName);
        Assert.False(alert.IsResolved);
        Assert.Contains((AlertingService.ApiKeyAlertSubject(soon), AlertTypes.ApiKeyExpiring), notifier.Sent);
    }

    [Fact]
    public async Task RevokingTheKey_ResolvesItsAlert()
    {
        var soon = await SeedKeyAsync("revoked", DateTimeOffset.UtcNow.AddDays(5));
        var service = CreateService(new FakeAlertWebhookClient());
        await service.CheckPinnedDomainsAsync();
        await using (var context = CreateContext())
        {
            await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, soon.Id);
        }

        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.ApiKeyExpiring)).IsResolved);
    }

    [Fact]
    public async Task OnceTheKeyHasExpired_ItsAlertResolves()
    {
        var expiring = await SeedKeyAsync("lapsed", DateTimeOffset.UtcNow.AddDays(3));
        var service = CreateService(new FakeAlertWebhookClient());
        await service.CheckPinnedDomainsAsync();
        await using (var context = CreateContext())
        {
            await context.ApiKeys.Where(key => key.Id == expiring.Id).ExecuteUpdateAsync(setters => setters.SetProperty(key => key.ExpiresUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.ApiKeyExpiring)).IsResolved);
    }

    [Fact]
    public async Task AnExpiringKey_IsAnnouncedOnce_NotEveryCooldown()
    {
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.CooldownMinutes = 0;
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
        }

        await SeedKeyAsync("announced", DateTimeOffset.UtcNow.AddDays(10));
        var notifier = new FakeAlertWebhookClient();
        var service = CreateService(notifier);

        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.AlertEvents.CountAsync(candidate => candidate.AlertType == AlertTypes.ApiKeyExpiring));
        Assert.Equal(1, notifier.Sent.Count(sent => sent.AlertType == AlertTypes.ApiKeyExpiring));
    }
}
