using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using DotMarc.Reporting;
using DotMarc.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class SummaryEndpoints
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/summary", GetSummaryAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("GetSummary")
            .WithSummary("Get the dashboard's headline figures")
            .WithDescription("Over the domains this key can see: how many, the overall 30-day pass rate, warnings, missing reports, reporters, why failing mail was let through or rejected, and how many domains are in each client portal status. parseFailureCount is only for keys with DomainsEdit not limited to certain groups.");

    private static async Task<Ok<ApiSummary>> GetSummaryAsync(ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domains = await scope.Domains(context.Domains.AsNoTracking()).Include(domain => domain.Groups).Include(domain => domain.Tags).AsSplitQuery().ToListAsync(cancellationToken);
        var rules = await AlertRuleSet.LoadAsync(context, cancellationToken);
        var ids = domains.Select(domain => domain.Id).ToList();
        var cutoffUtc = DomainStatistics.GetWindowCutoffUtc(timeProvider.GetUtcNow());
        var passRates = await DomainReadEndpoints.PassRatesAsync(context, ids, cutoffUtc, cancellationToken);
        var records = context.ReportRecords.AsNoTracking().Where(record => ids.Contains(record.Report.DomainId) && record.Report.ReceivedUtc >= cutoffUtc);
        var totals = await records.GroupBy(record => 1)
            .Select(grouping => new
            {
                Total = grouping.Sum(record => (long)record.MessageCount),
                Passing = grouping.Sum(record => record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass ? (long)record.MessageCount : 0L),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var sourceCount = await context.Reports.AsNoTracking().Where(report => ids.Contains(report.DomainId) && report.ReceivedUtc >= cutoffUtc)
            .Select(report => report.ReportingOrg).Distinct().CountAsync(cancellationToken);
        var actedOn = await records.Where(record => record.Disposition != DispositionResult.None)
            .Include(record => record.OverrideReasons).Include(record => record.AuthDetails).AsSplitQuery().ToListAsync(cancellationToken);
        var openAlerts = await PortalData.OpenAlertsAsync(context, domains.Select(domain => domain.Name).ToList(), cancellationToken);

        var statuses = domains.Select(domain => DashboardSummary.StatusOf(domain, passRates.TryGetValue(domain.Id, out var rate) ? rate : null, rules, timeProvider.GetUtcNow())).ToList();
        var healths = domains.Select(domain => PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, openAlerts[domain.Name].ToList()).Health).ToList();
        var breakdown = DomainStatistics.GetReasonBreakdown(actedOn);
        var mayCountParseFailures = !scope.IsScoped && user.HasClaim(UserAccessClaimsTransformation.PermissionClaimType, nameof(Permission.DomainsEdit));
        return TypedResults.Ok(new ApiSummary(
            domains.Count,
            totals is { Total: > 0 } ? (double)totals.Passing / totals.Total : 0,
            statuses.Count(status => status == "Warning"),
            statuses.Count(status => status == "Missing"),
            sourceCount,
            new ApiReasonBreakdown(breakdown.BenignOverride, breakdown.LocalPolicy, breakdown.Other, breakdown.NoReasonGiven,
                breakdown.InferredSpfFailure, breakdown.InferredDkimFailure, breakdown.InferredBothFailure),
            mayCountParseFailures ? await context.ParseFailures.CountAsync(cancellationToken) : null,
            new ApiStatusCounts(healths.Count(health => health == PortalHealth.Protected), healths.Count(health => health == PortalHealth.MonitoringOnly),
                healths.Count(health => health == PortalHealth.NeedsAttention), healths.Count(health => health == PortalHealth.NoReportsYet))));
    }
}
