using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The alerts raised outside the polling cycle (TLS failures, null-routed activity) and the system alerts (client report,
/// PSA connection, API key), now decided by rules too.</summary>
[Collection("Postgres")]
public sealed class AlertRuleEventAlertingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertRuleEventAlertingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings
        {
            Enabled = true, TeamsEnabled = true, TeamsWebhookUrl = "https://example.test/webhook", CooldownMinutes = 180
        });
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
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

    private AlertingService CreateService() =>
        new(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

    private async Task<int> AddRuleAsync(string kind, int position = -10, bool enabled = true, string severity = "Warning", int? cooldownMinutes = null, params AlertRuleScopeEntry[] scope)
    {
        await using var context = CreateContext();
        var rule = new AlertRule { Kind = kind, Name = $"Rule at {position}", Severity = severity, Position = position, Enabled = enabled, CooldownMinutes = cooldownMinutes, Scope = [.. scope] };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();
        return rule.Id;
    }

    private async Task DisableDefaultRuleAsync(string kind)
    {
        await using var context = CreateContext();
        (await context.AlertRules.SingleAsync(rule => rule.DefaultKey == kind)).Enabled = false;
        await context.SaveChangesAsync();
    }

    private async Task<int> SeedDomainInGroupAsync(string domainName, string groupName)
    {
        await using var context = CreateContext();
        var group = new Group { Name = groupName };
        context.Domains.Add(new Domain { Name = domainName, IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30), Groups = [group] });
        await context.SaveChangesAsync();
        return group.Id;
    }

    private async Task<List<AlertEvent>> AlertsAsync(string alertType)
    {
        await using var context = CreateContext();
        return await context.AlertEvents.Where(alert => alert.AlertType == alertType).OrderBy(alert => alert.Id).ToListAsync();
    }

    [Fact]
    public async Task ATlsrptRule_ScopedToAGroup_OnlyAlertsThatGroupsDomains()
    {
        var auroraGroupId = await SeedDomainInGroupAsync("aurora.test", "Aurora");
        await SeedDomainInGroupAsync("other.test", "Other");
        await DisableDefaultRuleAsync(AlertTypes.TlsrptFailure);
        await AddRuleAsync(AlertTypes.TlsrptFailure, scope: new AlertRuleScopeEntry { Target = AlertRuleScopeTarget.Group, TargetId = auroraGroupId });
        var service = CreateService();

        await service.HandleTlsrptReportAsync("aurora.test", 3, ["certificate-expired"]);
        await service.HandleTlsrptReportAsync("other.test", 3, ["certificate-expired"]);

        var alert = Assert.Single(await AlertsAsync(AlertTypes.TlsrptFailure));
        Assert.Equal("aurora.test", alert.DomainName);
    }

    [Fact]
    public async Task ATlsrptAlert_IsClosedByTheNextCycle_WhenItsRuleIsDisabled()
    {
        await SeedDomainInGroupAsync("aurora.test", "Aurora");
        var service = CreateService();
        await service.HandleTlsrptReportAsync("aurora.test", 3, ["certificate-expired"]);
        Assert.False(Assert.Single(await AlertsAsync(AlertTypes.TlsrptFailure)).IsResolved);

        await DisableDefaultRuleAsync(AlertTypes.TlsrptFailure);
        await service.CheckPinnedDomainsAsync();

        Assert.True(Assert.Single(await AlertsAsync(AlertTypes.TlsrptFailure)).IsResolved);
    }

    [Fact]
    public async Task ADisabledTlsrptRule_RaisesNothing()
    {
        await DisableDefaultRuleAsync(AlertTypes.TlsrptFailure);

        await CreateService().HandleTlsrptReportAsync("any.test", 3, ["certificate-expired"]);

        Assert.Empty(await AlertsAsync(AlertTypes.TlsrptFailure));
    }

    [Fact]
    public async Task TheDefaultTlsrptRule_RaisesAsBefore_AndStoresItsRuleId()
    {
        var service = CreateService();

        await service.HandleTlsrptReportAsync("any.test", 3, ["certificate-expired"]);
        await service.HandleTlsrptReportAsync("any.test", 3, ["certificate-expired"]);

        await using var context = CreateContext();
        var defaultRuleId = await context.AlertRules.Where(rule => rule.DefaultKey == AlertTypes.TlsrptFailure).Select(rule => rule.Id).SingleAsync();
        var alert = Assert.Single(await AlertsAsync(AlertTypes.TlsrptFailure));
        Assert.Equal(defaultRuleId, alert.AlertRuleId);
    }

    [Fact]
    public async Task ANullRoutedActivityRule_UsesItsOwnSeverity()
    {
        await AddRuleAsync(AlertTypes.UnexpectedActivityOnNullRoutedDomain, severity: "Info");

        await CreateService().FlagUnexpectedActivityForNullRoutedDomainAsync("null.test", new ReasonBreakdown(0, 0, 0, 0));

        var alert = Assert.Single(await AlertsAsync(AlertTypes.UnexpectedActivityOnNullRoutedDomain));
        Assert.Equal("Info", alert.Severity);
    }

    [Theory]
    [InlineData(AlertTypes.ApiKeyExpiring)]
    [InlineData(AlertTypes.ClientReportFailed)]
    [InlineData(AlertTypes.PsaConnectionLost)]
    public async Task ASystemKindRule_Disabled_RaisesNothing(string kind)
    {
        await DisableDefaultRuleAsync(kind);
        var service = CreateService();

        switch (kind)
        {
            case AlertTypes.ApiKeyExpiring:
                await SeedExpiringKeyAsync();
                await service.CheckPinnedDomainsAsync();
                break;
            case AlertTypes.ClientReportFailed:
                await service.RaiseClientReportFailedAsync(5, "Aurora", "weekly", "smtp down");
                break;
            default:
                await service.RaisePsaConnectionLostAsync(PsaKind.HaloPsa, "token refresh failed");
                break;
        }

        Assert.Empty(await AlertsAsync(kind));
    }

    [Fact]
    public async Task AClientReportFailedRule_SeverityAppearsOnTheAlert()
    {
        await DisableDefaultRuleAsync(AlertTypes.ClientReportFailed);
        await AddRuleAsync(AlertTypes.ClientReportFailed, severity: "Info");

        await CreateService().RaiseClientReportFailedAsync(5, "Aurora", "weekly", "smtp down");

        Assert.Equal("Info", Assert.Single(await AlertsAsync(AlertTypes.ClientReportFailed)).Severity);
    }

    [Fact]
    public async Task ThePsaConnectionAlert_StillRaisesOnceAndResolves_WithTheDefaultRule()
    {
        var service = CreateService();

        await service.RaisePsaConnectionLostAsync(PsaKind.HaloPsa, "token refresh failed");
        await service.RaisePsaConnectionLostAsync(PsaKind.HaloPsa, "token refresh failed");
        await service.ResolvePsaConnectionLostAsync(PsaKind.HaloPsa);

        var alert = Assert.Single(await AlertsAsync(AlertTypes.PsaConnectionLost));
        Assert.True(alert.IsResolved);
        Assert.NotNull(alert.AlertRuleId);
    }

    [Fact]
    public async Task TheApiKeyAlert_KeepsItsFourteenDayCooldown_ByDefault_AndARuleCooldownOverridesIt()
    {
        await SeedExpiringKeyAsync();
        var service = CreateService();

        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();
        Assert.Single(await AlertsAsync(AlertTypes.ApiKeyExpiring));

        // A rule with a zero cooldown raises on every cycle, which the 14 day default would never do.
        await DisableDefaultRuleAsync(AlertTypes.ApiKeyExpiring);
        await AddRuleAsync(AlertTypes.ApiKeyExpiring, cooldownMinutes: 0);
        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();

        var alerts = await AlertsAsync(AlertTypes.ApiKeyExpiring);
        Assert.True(alerts.Count >= 3, $"expected the zero cooldown rule to raise again, saw {alerts.Count} alerts");
    }

    private async Task SeedExpiringKeyAsync()
    {
        await using var context = CreateContext();
        context.ApiKeys.Add(new ApiKey
        {
            Name = "soon", Prefix = "dmk_soon", Hash = Guid.NewGuid().ToString("N"),
            Role = new Role { Name = "role for soon", Permissions = [Permission.DomainsView] },
            CreatedBy = "Test Admin", CreatedUtc = DateTimeOffset.UtcNow.AddDays(-80), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(5)
        });
        await context.SaveChangesAsync();
    }
}
