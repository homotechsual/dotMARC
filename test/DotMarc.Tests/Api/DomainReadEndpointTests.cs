using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class DomainReadEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public DomainReadEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static Report ReportWith(DateTimeOffset receivedUtc, params (string SourceIp, int MessageCount, AuthResult Spf, AuthResult Dkim)[] records)
    {
        var report = new Report
        {
            ReportingOrg = "google.com",
            ReportId = Guid.NewGuid().ToString(),
            DateRangeBeginUtc = receivedUtc.AddDays(-1),
            DateRangeEndUtc = receivedUtc,
            RawXml = "<feedback/>",
            ReceivedUtc = receivedUtc,
            AuthDetailBackfilledUtc = receivedUtc,
        };
        foreach (var (sourceIp, messageCount, spf, dkim) in records)
        {
            report.Records.Add(new ReportRecord { SourceIp = sourceIp, MessageCount = messageCount, Disposition = DispositionResult.None, SpfResult = spf, DkimResult = dkim, HeaderFrom = "example" });
        }

        return report;
    }

    [Fact]
    public async Task ListDomains_ReturnsGroupsTagsAndThe30DayPassRate()
    {
        var groupId = await _host.SeedGroupAsync("api-read-list");
        var tagId = await _host.SeedTagAsync("api-read-tag");
        var domainId = await _host.SeedDomainAsync("api-read-list.example", groupIds: [groupId], tagIds: [tagId], configure: domain =>
        {
            domain.LastReportReceivedUtc = DateTimeOffset.UtcNow.AddHours(-2);
            domain.Reports.Add(ReportWith(DateTimeOffset.UtcNow.AddHours(-2), ("203.0.113.1", 30, AuthResult.Pass, AuthResult.Fail), ("203.0.113.2", 10, AuthResult.Fail, AuthResult.Fail)));
            domain.Reports.Add(ReportWith(DateTimeOffset.UtcNow.AddDays(-40), ("203.0.113.3", 500, AuthResult.Fail, AuthResult.Fail)));
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiDomain>>($"/api/v1/domains?group={groupId}");

        var domain = Assert.Single(page!.Items);
        Assert.Equal(domainId, domain.Id);
        Assert.Equal("api-read-list.example", domain.Name);
        Assert.True(domain.Monitored);
        Assert.Equal([new ApiNamedRef(groupId, "api-read-list")], domain.Groups);
        Assert.Equal([new ApiNamedRef(tagId, "api-read-tag")], domain.Tags);
        Assert.Equal(0.75, domain.PassRate!.Value, precision: 6);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListDomains_PagesAndFiltersByTagAndMonitoring()
    {
        var tagId = await _host.SeedTagAsync("api-read-paging");
        await _host.SeedDomainAsync("api-page-a.example", tagIds: [tagId]);
        await _host.SeedDomainAsync("api-page-b.example", tagIds: [tagId]);
        await _host.SeedDomainAsync("api-page-c.example", tagIds: [tagId], monitored: false);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var firstPage = await client.GetFromJsonAsync<ApiPage<ApiDomain>>($"/api/v1/domains?tag={tagId}&monitored=true&pageSize=1");
        var secondPage = await client.GetFromJsonAsync<ApiPage<ApiDomain>>($"/api/v1/domains?tag={tagId}&monitored=true&pageSize=1&page=2");

        Assert.Equal(2, firstPage!.TotalCount);
        Assert.Equal(["api-page-a.example", "api-page-b.example"], firstPage.Items.Concat(secondPage!.Items).Select(domain => domain.Name).Order());
    }

    [Theory]
    [InlineData("pageSize=201", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    public async Task ListDomains_RefusesABadPage(string query, string field)
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync($"/api/v1/domains?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task AScopedKey_SeesOnlyItsDomains_AndOnlyItsGroupsOnThem()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scope-own");
        var otherGroupId = await _host.SeedGroupAsync("api-scope-other");
        var sharedDomainId = await _host.SeedDomainAsync("api-scope-shared.example", groupIds: [ownGroupId, otherGroupId]);
        var hiddenDomainId = await _host.SeedDomainAsync("api-scope-hidden.example", groupIds: [otherGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains");
        var hidden = await client.GetAsync($"/api/v1/domains/{hiddenDomainId}");

        var visible = Assert.Single(page!.Items);
        Assert.Equal(sharedDomainId, visible.Id);
        Assert.Equal([ownGroupId], visible.Groups.Select(group => group.Id));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task GetDomain_ReturnsTheStoredHealth()
    {
        var checkedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var domainId = await _host.SeedDomainAsync("api-health.example", configure: domain =>
        {
            domain.DmarcCheckStatus = DmarcCheckStatus.Ok;
            domain.DmarcCheckedUtc = checkedUtc;
            domain.SpfCheckStatus = SpfCheckStatus.Ok;
            domain.SpfCheckDetail = "v=spf1 -all";
            domain.DmarcPolicy = DmarcPolicyLevel.Reject;
            domain.DmarcPercent = 100;
            domain.DnsZone = "api-health.example";
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var detail = await client.GetFromJsonAsync<ApiDomainDetail>($"/api/v1/domains/{domainId}");

        Assert.Equal("Ok", detail!.Health.Dmarc.Status);
        Assert.Equal(checkedUtc.ToUnixTimeSeconds(), detail.Health.Dmarc.CheckedUtc!.Value.ToUnixTimeSeconds());
        Assert.Equal("v=spf1 -all", detail.Health.Spf.Detail);
        Assert.Equal("Reject", detail.DmarcPolicy.Policy);
        Assert.Equal(100, detail.DmarcPolicy.Percent);
        Assert.Equal("api-health.example", detail.DnsProvider.Zone);
    }

    [Fact]
    public async Task GetDomain_Unknown_Is404()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/domains/987654");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ReportSummary_TotalsTheWindow_AndRanksSources()
    {
        var domainId = await _host.SeedDomainAsync("api-summary.example", configure: domain =>
        {
            domain.Reports.Add(ReportWith(DateTimeOffset.UtcNow.AddDays(-2), ("203.0.113.10", 5, AuthResult.Pass, AuthResult.Pass), ("203.0.113.11", 20, AuthResult.Fail, AuthResult.Fail)));
            domain.Reports.Add(ReportWith(DateTimeOffset.UtcNow.AddDays(-10), ("203.0.113.12", 100, AuthResult.Pass, AuthResult.Pass)));
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var summary = await client.GetFromJsonAsync<ApiReportSummary>($"/api/v1/domains/{domainId}/reports/summary?days=7");

        Assert.Equal(7, summary!.Days);
        Assert.Equal(25, summary.TotalVolume);
        Assert.Equal(0.2, summary.PassRate!.Value, precision: 6);
        Assert.Equal(["203.0.113.11", "203.0.113.10"], summary.TopSources.Select(source => source.SourceIp));
        Assert.Equal("Fail", summary.TopSources[0].Spf);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task ReportSummary_RefusesAWindowOutside1To30Days(int days)
    {
        var domainId = await _host.SeedDomainAsync($"api-summary-{days}.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync($"/api/v1/domains/{domainId}/reports/summary?days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AScopedKey_FilteringByAGroupOutsideItsScope_GetsNothing()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-filter-own");
        var hiddenGroupId = await _host.SeedGroupAsync("api-filter-hidden");
        await _host.SeedDomainAsync("api-filter-shared.example", groupIds: [ownGroupId, hiddenGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiDomain>>($"/api/v1/domains?group={hiddenGroupId}");

        Assert.Empty(page!.Items);
        Assert.Equal(0, page.TotalCount);
    }
}
