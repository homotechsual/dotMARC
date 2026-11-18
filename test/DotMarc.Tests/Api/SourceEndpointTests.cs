using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class SourceEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public SourceEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static Report ReportWith(DateTimeOffset receivedUtc, params (string SourceIp, int Count, AuthResult Spf, AuthResult Dkim, DispositionResult Disposition)[] records)
    {
        var report = new Report
        {
            ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = receivedUtc.AddDays(-1), DateRangeEndUtc = receivedUtc,
            RawXml = "<feedback/>", ReceivedUtc = receivedUtc, AuthDetailBackfilledUtc = receivedUtc,
        };
        foreach (var (sourceIp, count, spf, dkim, disposition) in records)
        {
            report.Records.Add(new ReportRecord { SourceIp = sourceIp, MessageCount = count, Disposition = disposition, SpfResult = spf, DkimResult = dkim, HeaderFrom = "example" });
        }

        return report;
    }

    private async Task<int> SeedMixedAsync(string name)
    {
        var received = DateTimeOffset.UtcNow.AddDays(-2);
        return await _host.SeedDomainAsync(name, configure: domain => domain.Reports.Add(ReportWith(received,
            ("192.0.2.30", 50, AuthResult.Pass, AuthResult.Pass, DispositionResult.None),
            ("192.0.2.31", 10, AuthResult.Fail, AuthResult.Fail, DispositionResult.Quarantine),
            ("192.0.2.31", 4, AuthResult.Pass, AuthResult.Fail, DispositionResult.None),
            ("192.0.2.32", 6, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject))));
    }

    [Fact]
    public async Task Sources_MatchDomainStatistics_BusiestFirst()
    {
        var domainId = await SeedMixedAsync("api-sources.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>("/api/v1/domains/api-sources.example/sources");

        await using var context = _host.CreateContext();
        var reports = await context.Reports.Where(report => report.DomainId == domainId).Include(report => report.Records).ToListAsync();
        var expected = DomainStatistics.GetSourceAggregates(reports).OrderByDescending(source => source.Volume).ThenBy(source => source.SourceIp)
            .Select(source => (source.SourceIp, source.Volume, source.SpfResult.ToString(), source.DkimResult.ToString(), source.Disposition.ToString()));
        Assert.Equal(expected, page!.Items.Select(source => (source.Ip, source.Volume, source.Spf, source.Dkim, source.Disposition)));
        var mixed = page.Items.Single(source => source.Ip == "192.0.2.31");
        Assert.Equal((14, 4, 10), (mixed.Volume, mixed.Passed, mixed.Failed));
        Assert.Equal(4d / 14, mixed.PassRate!.Value, precision: 6);
    }

    [Fact]
    public async Task Sources_NameTheSendingService_OrAreNull_WhenTheReportsDontPointToOne()
    {
        var received = DateTimeOffset.UtcNow.AddDays(-2);
        await _host.SeedDomainAsync("api-sources-service.example", configure: domain =>
        {
            var report = ReportWith(received,
                ("198.51.100.7", 20, AuthResult.Fail, AuthResult.Fail, DispositionResult.None),
                ("203.0.113.10", 5, AuthResult.Pass, AuthResult.Pass, DispositionResult.None));
            report.Records[0].AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "bounce.mcsv.net", Result = DmarcMechanismResult.Pass });
            domain.Reports.Add(report);
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>("/api/v1/domains/api-sources-service.example/sources");

        Assert.Equal("Mailchimp", page!.Items.Single(source => source.Ip == "198.51.100.7").Service);
        Assert.Null(page.Items.Single(source => source.Ip == "203.0.113.10").Service);
    }

    private async Task<(int DomainId, int GroupId)> SeedMailchimpDomainAsync(string name)
    {
        var group = new Group { Name = $"Group of {name}" };
        var domainId = await _host.SeedDomainAsync(name, configure: domain =>
        {
            domain.Groups.Add(group);
            var report = ReportWith(DateTimeOffset.UtcNow.AddDays(-2),
                ("198.51.100.7", 20, AuthResult.Fail, AuthResult.Fail, DispositionResult.None),
                ("203.0.113.10", 5, AuthResult.Pass, AuthResult.Pass, DispositionResult.None));
            report.Records[0].AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "bounce.mcsv.net", Result = DmarcMechanismResult.Pass });
            domain.Reports.Add(report);
        });
        await using var context = _host.CreateContext();
        return (domainId, (await context.Groups.SingleAsync(candidate => candidate.Name == group.Name)).Id);
    }

    private async Task DecideAsync(SenderDecisionScope scope, SenderDecisionStatus status, string key, SenderDecisionKind kind = SenderDecisionKind.Service, int? domainId = null, int? groupId = null)
    {
        await using var context = _host.CreateContext();
        context.SenderDecisions.Add(new SenderDecision
        {
            Scope = scope, Kind = kind, Key = key, Status = status, DomainId = scope == SenderDecisionScope.Domain ? domainId : null,
            GroupId = scope == SenderDecisionScope.Group ? groupId : null, DecidedBy = "u", DecidedByName = "Sam Lee",
        });
        await context.SaveChangesAsync();
    }

    private async Task<Dictionary<string, string?>> DecisionsAsync(string domainName)
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);
        var page = await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>($"/api/v1/domains/{domainName}/sources");
        return page!.Items.ToDictionary(source => source.Ip, source => source.Decision);
    }

    [Fact]
    public async Task Sources_ReturnUnreviewed_WhenNoDecisionApplies()
    {
        await SeedMailchimpDomainAsync("api-decision-none.example");

        var decisions = await DecisionsAsync("api-decision-none.example");

        Assert.Equal(["Unreviewed", "Unreviewed"], decisions.Values);
    }

    [Fact]
    public async Task Sources_ReturnTheDomainDecision_ForAServiceMatchedIp()
    {
        var (domainId, _) = await SeedMailchimpDomainAsync("api-decision-domain.example");
        await DecideAsync(SenderDecisionScope.Domain, SenderDecisionStatus.Unauthorised, "Mailchimp", domainId: domainId);

        var decisions = await DecisionsAsync("api-decision-domain.example");

        Assert.Equal(("Unauthorised", "Unreviewed"), (decisions["198.51.100.7"], decisions["203.0.113.10"]));
    }

    [Fact]
    public async Task Sources_AnIpDecisionOverridesTheServiceDecision()
    {
        var (domainId, _) = await SeedMailchimpDomainAsync("api-decision-ip.example");
        await DecideAsync(SenderDecisionScope.Domain, SenderDecisionStatus.Authorised, "Mailchimp", domainId: domainId);
        await DecideAsync(SenderDecisionScope.Domain, SenderDecisionStatus.Unauthorised, "198.51.100.7", SenderDecisionKind.Ip, domainId: domainId);

        Assert.Equal("Unauthorised", (await DecisionsAsync("api-decision-ip.example"))["198.51.100.7"]);
    }

    [Fact]
    public async Task Sources_UseTheBaselineAndGroupDecisions()
    {
        var (_, groupId) = await SeedMailchimpDomainAsync("api-decision-levels.example");
        await DecideAsync(SenderDecisionScope.Baseline, SenderDecisionStatus.UnderReview, "Mailchimp");
        Assert.Equal("UnderReview", (await DecisionsAsync("api-decision-levels.example"))["198.51.100.7"]);

        await DecideAsync(SenderDecisionScope.Group, SenderDecisionStatus.Authorised, "Mailchimp", groupId: groupId);

        Assert.Equal("Authorised", (await DecisionsAsync("api-decision-levels.example"))["198.51.100.7"]);
    }

    [Fact]
    public async Task Sources_ReturnUnreviewed_ForAnUnmatchedIp_WithOnlyAServiceBaseline()
    {
        await SeedMailchimpDomainAsync("api-decision-unmatched.example");
        await DecideAsync(SenderDecisionScope.Baseline, SenderDecisionStatus.Authorised, "Mailchimp");

        Assert.Equal("Unreviewed", (await DecisionsAsync("api-decision-unmatched.example"))["203.0.113.10"]);
    }

    [Fact]
    public async Task Filters_NarrowSources()
    {
        await SeedMixedAsync("api-sources-filter.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);
        async Task<IEnumerable<string>> IpsFor(string query) =>
            (await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>($"/api/v1/domains/api-sources-filter.example/sources?{query}"))!.Items.Select(source => source.Ip);

        Assert.Equal(["192.0.2.30"], await IpsFor("result=passing"));
        Assert.Equal(["192.0.2.31", "192.0.2.32"], await IpsFor("result=FAILING"));
        Assert.Equal(["192.0.2.32"], await IpsFor("disposition=reject"));
        Assert.Equal(["192.0.2.31"], await IpsFor("disposition=quarantine&spf=pass"));
        Assert.Equal(["192.0.2.30", "192.0.2.31"], await IpsFor("minVolume=10"));
        Assert.Equal(["192.0.2.32"], await IpsFor("ip=.32"));
    }

    [Fact]
    public async Task Organization_ComesFromTheCache_AndFilters_WithPagingCountingMatches()
    {
        await SeedMixedAsync("api-sources-org.example");
        await using (var context = _host.CreateContext())
        {
            context.IpInfos.Add(new IpInfo { Ip = "192.0.2.30", Organization = "Contoso Mail", Country = "GB", Status = IpLookupStatus.Ok, LookedUpUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>("/api/v1/domains/api-sources-org.example/sources");
        var contoso = await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>("/api/v1/domains/api-sources-org.example/sources?organization=contoso&pageSize=1");

        Assert.Equal(("Contoso Mail", "GB"), (all!.Items.Single(source => source.Ip == "192.0.2.30").Organization, all.Items.Single(source => source.Ip == "192.0.2.30").Country));
        Assert.Null(all.Items.Single(source => source.Ip == "192.0.2.31").Organization);
        Assert.Equal(1, contoso!.TotalCount);
        Assert.Equal(["192.0.2.30"], contoso.Items.Select(source => source.Ip));
    }

    [Fact]
    public async Task BadFilters_AndAnUnseenDomain()
    {
        await SeedMixedAsync("api-sources-bad.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var bad = await client.GetAsync("/api/v1/domains/api-sources-bad.example/sources?result=maybe&days=7&from=2026-01-01");
        var unseen = await client.GetAsync("/api/v1/domains/nowhere-at-all.example/sources");

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Contains("\"result\"", body);
        Assert.Contains("\"days\"", body);
        Assert.Equal(HttpStatusCode.NotFound, unseen.StatusCode);
    }
}
