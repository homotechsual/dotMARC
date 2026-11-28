using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class SummaryEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public SummaryEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task MissingCount_UsesTheDomainsScopedRuleThreshold()
    {
        var group = await _host.SeedGroupAsync("cadence-group");
        await _host.SeedDomainAsync("cadence.example", groupIds: [group]);
        await using (var context = _host.CreateContext())
        {
            var domain = await context.Domains.SingleAsync(domain => domain.Name == "cadence.example");
            domain.IsMonitored = true;
            domain.FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-20);
            domain.LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-4);
            context.AlertRules.Add(new AlertRule
            {
                Kind = AlertTypes.MissedReport, Name = "Weekly cadence", Enabled = true, Severity = "Warning", StopProcessing = true,
                Position = -100, ParametersJson = "{\"days\":\"7\"}",
                Scope = [new() { Target = AlertRuleScopeTarget.Group, TargetId = group }]
            });
            await context.SaveChangesAsync();
        }
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView], scopedGroupIds: [group]);
        using var client = _host.ClientFor(secret);
        Assert.Equal(0, (await client.GetFromJsonAsync<ApiSummary>("/api/v1/summary"))!.MissingCount);
        await using (var context = _host.CreateContext())
        {
            var rule = await context.AlertRules.SingleAsync(rule => rule.Name == "Weekly cadence");
            rule.ParametersJson = "{\"days\":\"3\"}";
            await context.SaveChangesAsync();
        }
        Assert.Equal(1, (await client.GetFromJsonAsync<ApiSummary>("/api/v1/summary"))!.MissingCount);
    }

    [Fact]
    public async Task TheSummary_MatchesTheDashboard_ForEveryDomainAKeyCanSee()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView, Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var summary = await client.GetFromJsonAsync<ApiSummary>("/api/v1/summary");

        await using var context = _host.CreateContext();
        var cutoff = DomainStatistics.GetWindowCutoffUtc();
        var domains = await context.Domains.AsNoTracking()
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.OverrideReasons)
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.AuthDetails)
            .AsSplitQuery().ToListAsync();
        var (expected, _) = DashboardSummary.Build(domains, await context.ParseFailures.CountAsync());
        Assert.Equal((expected.DomainCount, expected.WarningCount, expected.MissingCount, expected.SourceCount, expected.ParseFailureCount),
            (summary!.DomainCount, summary.WarningCount, summary.MissingCount, summary.SourceCount, summary.ParseFailureCount!.Value));
        Assert.Equal(expected.OverallPassRate, summary.OverallPassRate, precision: 6);
        Assert.Equal(expected.ReasonBreakdown.BenignOverride, summary.ReasonBreakdown.BenignOverride);
        var openAlerts = (await context.AlertEvents.AsNoTracking().Where(alert => !alert.IsResolved).ToListAsync()).ToLookup(alert => alert.DomainName);
        var healths = domains.Select(domain => PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, openAlerts[domain.Name].ToList()).Health).ToList();
        Assert.Equal(new ApiStatusCounts(healths.Count(health => health == PortalHealth.Protected), healths.Count(health => health == PortalHealth.MonitoringOnly),
            healths.Count(health => health == PortalHealth.NeedsAttention), healths.Count(health => health == PortalHealth.NoReportsYet)), summary.StatusCounts);
    }

    [Fact]
    public async Task AGroupLimitedKey_GetsItsOwnFigures_AndNoParseFailureCount()
    {
        var group = await _host.SeedGroupAsync("api-summary-own");
        await _host.SeedDomainAsync("api-summary-own1.example", groupIds: [group]);
        await _host.SeedDomainAsync("api-summary-own2.example", groupIds: [group]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView, Permission.DomainsEdit], scopedGroupIds: [group]);
        using var client = _host.ClientFor(secret);

        var summary = await client.GetFromJsonAsync<ApiSummary>("/api/v1/summary");

        Assert.Equal(2, summary!.DomainCount);
        Assert.Null(summary.ParseFailureCount);
        Assert.Equal(2, summary.StatusCounts.NoReportsYet);
    }

    [Fact]
    public async Task WithoutDomainsEdit_ThereIsNoParseFailureCount()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        Assert.Null((await client.GetFromJsonAsync<ApiSummary>("/api/v1/summary"))!.ParseFailureCount);
    }
}
