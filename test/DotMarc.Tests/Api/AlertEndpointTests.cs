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

    private async Task<int> SeedAlertAsync(string subject, string alertType, bool resolved = false, DateTimeOffset? createdUtc = null, string severity = "Warning")
    {
        await using var context = _host.CreateContext();
        var alert = new AlertEvent
        {
            DomainName = subject, AlertType = alertType, Severity = severity, Title = $"{alertType} title", Message = "message",
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

    [Fact]
    public async Task Filters_NarrowByTypeSeverityAndDomain_InAnyLetterCase()
    {
        var domainId = await _host.SeedDomainAsync("api-filter-alerts.example", monitored: false);
        var wanted = await SeedAlertAsync("api-filter-alerts.example", AlertTypes.MissedReport, severity: "Warning");
        await SeedAlertAsync("api-filter-alerts.example", AlertTypes.MissedReport, severity: "Info");
        await SeedAlertAsync("api-filter-alerts.example", AlertTypes.TlsrptFailure, severity: "Warning");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var byName = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-filter-alerts.example&type=missedreport&severity=WARNING");
        var byId = await client.GetFromJsonAsync<ApiPage<ApiAlert>>($"/api/v1/alerts?domain={domainId}&type=MissedReport,TlsrptFailure&severity=Warning");

        Assert.Equal([wanted], byName!.Items.Select(alert => alert.Id));
        Assert.Equal(2, byId!.TotalCount);
    }

    [Fact]
    public async Task ACriticalSeverityFilter_FindsCriticalAlerts_AndABogusOneIsRefusedNamingTheAllowedValues()
    {
        await _host.SeedDomainAsync("api-critical-alerts.example", monitored: false);
        var critical = await SeedAlertAsync("api-critical-alerts.example", AlertTypes.MissedReport, severity: "Critical");
        await SeedAlertAsync("api-critical-alerts.example", AlertTypes.TlsrptFailure, severity: "Warning");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var found = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-critical-alerts.example&severity=critical");
        var refused = await client.GetAsync("/api/v1/alerts?severity=Bogus");

        Assert.Equal([critical], found!.Items.Select(alert => alert.Id));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Critical", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Status_Resolved_And_ResolvedDates_FindClosedAlerts()
    {
        await _host.SeedDomainAsync("api-filter-resolved.example", monitored: false);
        var open = await SeedAlertAsync("api-filter-resolved.example", AlertTypes.MissedReport);
        var closed = await SeedAlertAsync("api-filter-resolved.example", AlertTypes.MxRecordBroken, resolved: true);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);
        var since = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        var resolved = await client.GetFromJsonAsync<ApiPage<ApiAlert>>($"/api/v1/alerts?domain=api-filter-resolved.example&status=resolved&resolvedAfter={since}");

        Assert.Equal([closed], resolved!.Items.Select(alert => alert.Id));
        Assert.DoesNotContain(open, resolved.Items.Select(alert => alert.Id));
    }

    [Fact]
    public async Task GroupFilter_IgnoresAGroupOutsideAScopedKeysGroups()
    {
        var ownGroup = await _host.SeedGroupAsync("api-filter-alerts-own");
        var otherGroup = await _host.SeedGroupAsync("api-filter-alerts-other");
        await _host.SeedDomainAsync("api-filter-own.example", groupIds: [ownGroup], monitored: false);
        await _host.SeedDomainAsync("api-filter-other.example", groupIds: [otherGroup, ownGroup], monitored: false);
        var ownAlert = await SeedAlertAsync("api-filter-own.example", AlertTypes.MissedReport);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView], scopedGroupIds: [ownGroup]);
        using var client = _host.ClientFor(secret);

        var outsideOnly = await client.GetFromJsonAsync<ApiPage<ApiAlert>>($"/api/v1/alerts?group={otherGroup}");
        var both = await client.GetFromJsonAsync<ApiPage<ApiAlert>>($"/api/v1/alerts?group={otherGroup},{ownGroup}");

        Assert.Empty(outsideOnly!.Items);
        Assert.Contains(ownAlert, both!.Items.Select(alert => alert.Id));
    }

    [Fact]
    public async Task Acknowledgeable_And_RaisedDates_Filter()
    {
        await _host.SeedDomainAsync("api-filter-ack.example", monitored: false);
        var weakened = await SeedAlertAsync("api-filter-ack.example", AlertTypes.DmarcPolicyWeakened);
        var old = await SeedAlertAsync("api-filter-ack.example", AlertTypes.MissedReport, createdUtc: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var acknowledgeable = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-filter-ack.example&acknowledgeable=true");
        var january = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-filter-ack.example&raisedAfter=2026-01-01&raisedBefore=2026-02-01");

        Assert.Equal([weakened], acknowledgeable!.Items.Select(alert => alert.Id));
        Assert.Equal([old], january!.Items.Select(alert => alert.Id));
    }

    [Fact]
    public async Task BadFilters_AreA400NamingThem_AndAnUnseenDomainIsA404()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var badType = await client.GetAsync("/api/v1/alerts?type=NotAType&raisedAfter=2026-02-01&raisedBefore=2026-01-01");
        var unseen = await client.GetAsync("/api/v1/alerts?domain=nowhere-at-all.example");

        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        var body = await badType.Content.ReadAsStringAsync();
        Assert.Contains("\"type\"", body);
        Assert.Contains("MissedReport", body);
        Assert.Contains("Must be earlier than raisedBefore.", body);
        Assert.Equal(HttpStatusCode.NotFound, unseen.StatusCode);
    }

    [Fact]
    public async Task Alerts_CarryTheirTickets_AndFilterByAnOpenTicket()
    {
        await _host.SeedDomainAsync("api-tickets.example", monitored: false);
        var ticketed = await SeedAlertAsync("api-tickets.example", AlertTypes.MissedReport);
        var bare = await SeedAlertAsync("api-tickets.example", AlertTypes.TlsrptFailure);
        await using (var context = _host.CreateContext())
        {
            context.AlertTickets.Add(new DotMarc.Psa.AlertTicket { AlertEventId = ticketed, Psa = DotMarc.Psa.PsaKind.HaloPsa, TicketId = "T-1001", IsOpen = true, CreatedUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-tickets.example");
        var withTicket = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-tickets.example&hasOpenTicket=true");
        var without = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-tickets.example&hasOpenTicket=false");

        var ticket = Assert.Single(all!.Items.Single(alert => alert.Id == ticketed).Tickets);
        Assert.Equal(("HaloPsa", "T-1001", true), (ticket.Psa, ticket.TicketId, ticket.Open));
        Assert.Empty(all.Items.Single(alert => alert.Id == bare).Tickets);
        Assert.Equal([ticketed], withTicket!.Items.Select(alert => alert.Id));
        Assert.Equal([bare], without!.Items.Select(alert => alert.Id));
    }
}
