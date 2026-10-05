using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class AlertEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public AlertEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<int> SeedAlertAsync(string subject, string alertType, bool resolved = false, DateTimeOffset? createdUtc = null)
    {
        await using var context = _host.CreateContext();
        var alert = new AlertEvent
        {
            DomainName = subject, AlertType = alertType, Severity = "Warning", Title = $"{alertType} title", Message = "message",
            IsResolved = resolved, ResolvedUtc = resolved ? DateTimeOffset.UtcNow : null, CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow,
        };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return alert.Id;
    }

    [Fact]
    public async Task ListAlerts_ShowsOpenAlertsNewestFirst_ForAScopedKeysDomains()
    {
        var groupId = await _host.SeedGroupAsync("api-alerts-own");
        // Unmonitored, and alert types the demo host's background monitor never raises or resolves for it, so only this
        // test changes these alerts.
        var domainId = await _host.SeedDomainAsync("api-alerts.example", groupIds: [groupId], monitored: false);
        var olderId = await SeedAlertAsync("api-alerts.example", AlertTypes.MissedReport, createdUtc: DateTimeOffset.UtcNow.AddHours(-3));
        var newerId = await SeedAlertAsync("api-alerts.example", AlertTypes.TlsrptFailure);
        var resolvedId = await SeedAlertAsync("api-alerts.example", AlertTypes.MxRecordBroken, resolved: true);
        await SeedAlertAsync("api-alerts-elsewhere.example", AlertTypes.MissedReport);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var open = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts");
        var all = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?status=all");

        Assert.Equal([newerId, olderId], open!.Items.Select(alert => alert.Id));
        Assert.Equal(new ApiNamedRef(domainId, "api-alerts.example"), open.Items[0].Domain);
        Assert.Equal("TLS delivery failures", open.Items[0].TypeName);
        Assert.Contains(resolvedId, all!.Items.Select(alert => alert.Id));
        Assert.Equal(3, all.TotalCount);
    }

    [Fact]
    public async Task AnUnscopedKey_SeesAlertsThatArentAboutADomain()
    {
        var alertId = await SeedAlertAsync("API key Old sync (dmk_abcdefgh)", AlertTypes.ApiKeyExpiring);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?pageSize=200");

        var alert = Assert.Single(page!.Items, candidate => candidate.Id == alertId);
        Assert.Null(alert.Domain);
        Assert.Equal("API key Old sync (dmk_abcdefgh)", alert.Subject);
    }

    [Fact]
    public async Task Acknowledge_ClosesAPolicyAlert_AsTheKey()
    {
        await _host.SeedDomainAsync("api-ack.example", configure: domain =>
        {
            domain.DmarcPolicy = DmarcPolicyLevel.None;
            domain.DmarcSubdomainPolicy = DmarcPolicyLevel.None;
            domain.DmarcPercent = 100;
            domain.AlertStates.Add(new DomainAlertState { Item = DnsHealthItems.DmarcPolicy, Baseline = "p=reject; sp=reject; pct=100" });
        });
        var alertId = await SeedAlertAsync("api-ack.example", AlertTypes.DmarcPolicyWeakened);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsManage]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<ApiAcknowledgement>())!.TicketClosed);
        await using var context = _host.CreateContext();
        Assert.True((await context.AlertEvents.SingleAsync(alert => alert.Id == alertId)).IsResolved);
        var entry = await context.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.AlertAcknowledged && candidate.TargetName == "api-ack.example");
        Assert.Equal(AuditActorKind.ApiKey, entry.ActorKind);
    }

    [Fact]
    public async Task Acknowledge_ACheckAlert_Is409()
    {
        await _host.SeedDomainAsync("api-ack-check.example");
        var alertId = await SeedAlertAsync("api-ack-check.example", AlertTypes.SpfRecordBroken);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsManage]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Acknowledge_AnAlertOutsideAScopedKeysGroups_Is404()
    {
        var groupId = await _host.SeedGroupAsync("api-ack-scope");
        await _host.SeedDomainAsync("api-ack-hidden.example");
        var alertId = await SeedAlertAsync("api-ack-hidden.example", AlertTypes.NameserversChanged);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsManage], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Acknowledge_WithoutAlertsManage_Is403()
    {
        var alertId = await SeedAlertAsync("api-ack-denied.example", AlertTypes.NameserversChanged);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListAlerts_RefusesAnUnknownStatus()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/alerts?status=closed");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
