using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Reporting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class DomainReadEndpoints
{
    public const int MaximumPageSize = 200;
    private const int DefaultPageSize = 50;
    private const int TopSourceCount = 20;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/domains", ListDomainsAsync)
            .RequirePermission(Permission.DomainsView)
            .WithName("ListDomains")
            .WithSummary("List domains")
            .WithDescription($"Domains this key can see, in dashboard order, {DefaultPageSize} a page by default and at most {MaximumPageSize}. Filter by group id, tag id or monitoring. The pass rate covers the last 30 days and is null with no reports.");

        api.MapGet("/domains/{id:int}", GetDomainAsync)
            .RequirePermission(Permission.DomainsView)
            .WithName("GetDomain")
            .WithSummary("Get a domain and its health")
            .WithDescription("The domain with the results of dotMARC's last DNS checks. The API never runs checks itself; checkedUtc says when each last ran.");

        api.MapGet("/domains/{id:int}/reports/summary", GetReportSummaryAsync)
            .RequirePermission(Permission.DomainsView)
            .WithName("GetReportSummary")
            .WithSummary("Summarise a domain's DMARC reports")
            .WithDescription("Volume, pass rate, why failing mail was let through or rejected, and the 20 busiest sending IPs, over the last 1 to 30 days (default 30).");
    }

    /// <summary>One domain the scope can see, with what ApiDomain.From needs, or null.</summary>
    public static Task<Domain?> LoadForApiAsync(DotMarcDbContext context, ApiScope scope, int domainId, CancellationToken cancellationToken)
    {
        var cutoffUtc = DomainStatistics.GetWindowCutoffUtc();
        return scope.Domains(context.Domains.AsNoTracking())
            .Where(domain => domain.Id == domainId)
            .Include(domain => domain.Groups)
            .Include(domain => domain.Tags)
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoffUtc))
            .ThenInclude(report => report.Records)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static async Task<Results<Ok<ApiPage<ApiDomain>>, ValidationProblem>> ListDomainsAsync(
        ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, int? page, int? pageSize, int? group, int? tag, bool? monitored,
        CancellationToken cancellationToken)
    {
        var pageNumber = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (pageNumber < 1)
        {
            return ApiProblems.Validation("page", "Must be 1 or more.");
        }

        if (size is < 1 or > MaximumPageSize)
        {
            return ApiProblems.Validation("pageSize", $"Must be between 1 and {MaximumPageSize}.");
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = scope.Domains(context.Domains.AsNoTracking());
        if (group is { } groupId)
        {
            query = query.Where(domain => domain.Groups.Any(candidate => candidate.Id == groupId));
        }

        if (tag is { } tagId)
        {
            query = query.Where(domain => domain.Tags.Any(candidate => candidate.Id == tagId));
        }

        if (monitored is { } isMonitored)
        {
            query = query.Where(domain => domain.IsMonitored == isMonitored);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var cutoffUtc = DomainStatistics.GetWindowCutoffUtc();
        var domains = await query
            .OrderBy(domain => domain.SortOrder)
            .ThenBy(domain => domain.Name)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Include(domain => domain.Groups)
            .Include(domain => domain.Tags)
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoffUtc))
            .ThenInclude(report => report.Records)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new ApiPage<ApiDomain>(domains.Select(domain => ApiDomain.From(domain, scope)).ToList(), pageNumber, size, totalCount));
    }

    private static async Task<Results<Ok<ApiDomainDetail>, ProblemHttpResult>> GetDomainAsync(
        int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await LoadForApiAsync(context, scope, id, cancellationToken);
        return domain is null ? ApiProblems.NotFound($"domain {id}") : TypedResults.Ok(ApiDomainDetail.From(domain, scope));
    }

    private static async Task<Results<Ok<ApiReportSummary>, ValidationProblem, ProblemHttpResult>> GetReportSummaryAsync(
        int id, int? days, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var maximumDays = (int)DomainStatistics.ReportWindow.TotalDays;
        var windowDays = days ?? maximumDays;
        if (windowDays < 1 || windowDays > maximumDays)
        {
            return ApiProblems.Validation("days", $"Must be between 1 and {maximumDays}.");
        }

        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-windowDays);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await ApiScope.From(user).Domains(context.Domains.AsNoTracking())
            .Where(candidate => candidate.Id == id)
            .Include(candidate => candidate.Reports.Where(report => report.ReceivedUtc >= cutoffUtc))
            .ThenInclude(report => report.Records)
            .ThenInclude(record => record.OverrideReasons)
            .Include(candidate => candidate.Reports.Where(report => report.ReceivedUtc >= cutoffUtc))
            .ThenInclude(report => report.Records)
            .ThenInclude(record => record.AuthDetails)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);
        if (domain is null)
        {
            return ApiProblems.NotFound($"domain {id}");
        }

        var breakdown = DomainStatistics.GetReasonBreakdown(domain.Reports);
        var topSources = DomainStatistics.GetSourceAggregates(domain.Reports)
            .OrderByDescending(source => source.Volume)
            .ThenBy(source => source.SourceIp)
            .Take(TopSourceCount)
            .Select(source => new ApiSource(source.SourceIp, source.Volume, source.SpfResult.ToString(), source.DkimResult.ToString(), source.Disposition.ToString()))
            .ToList();
        return TypedResults.Ok(new ApiReportSummary(
            domain.Id, domain.Name, windowDays, DomainStatistics.GetTotalVolume(domain.Reports), DomainStatistics.GetPassRate(domain.Reports),
            new ApiReasonBreakdown(breakdown.BenignOverride, breakdown.LocalPolicy, breakdown.Other, breakdown.NoReasonGiven,
                breakdown.InferredSpfFailure, breakdown.InferredDkimFailure, breakdown.InferredBothFailure),
            topSources));
    }
}
