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
            .WithTags(ApiTags.Domains)
            .WithName("ListDomains")
            .WithSummary("List domains")
            .WithDescription($"Domains this key can see, in dashboard order, {DefaultPageSize} a page by default and at most {MaximumPageSize}. Filter by group id, tag id or monitoring. The pass rate covers the last 30 days and is null with no reports.");

        api.MapGet("/domains/{domain}", GetDomainAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Domains)
            .WithName("GetDomain")
            .WithSummary("Get a domain and its health")
            .WithDescription($"The domain with the results of dotMARC's last DNS checks. The API never runs checks itself; checkedUtc says when each last ran. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/domains/{domain}/reports/summary", GetReportSummaryAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Domains)
            .WithName("GetReportSummary")
            .WithSummary("Summarise a domain's DMARC reports")
            .WithDescription($"Volume, pass rate, why failing mail was let through or rejected, and the 20 busiest sending IPs, over the last 1 to 30 days (default 30). {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>One domain the scope can see, with its groups and tags, or null.</summary>
    public static Task<Domain?> LoadForApiAsync(DotMarcDbContext context, ApiScope scope, int domainId, CancellationToken cancellationToken) =>
        LoadForApiAsync(context, scope, domainId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    /// <summary>One domain the scope can see, by id or name (see <see cref="ApiDomainKey"/>), with its groups and tags,
    /// or null.</summary>
    public static Task<Domain?> LoadForApiAsync(DotMarcDbContext context, ApiScope scope, string domainKey, CancellationToken cancellationToken) =>
        ApiDomainKey.Matching(scope.Domains(context.Domains.AsNoTracking()), domainKey)
            .Include(domain => domain.Groups)
            .Include(domain => domain.Tags)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Each domain's volume-weighted pass rate since the cutoff, worked out by the database rather than by
    /// loading every record. A record passes when SPF or DKIM passed, as in DomainStatistics.GetPassRate. Domains with
    /// no volume are left out, meaning null.</summary>
    public static async Task<Dictionary<int, double>> PassRatesAsync(DotMarcDbContext context, IReadOnlyCollection<int> domainIds,
        DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        var totals = await context.ReportRecords
            .Where(record => domainIds.Contains(record.Report.DomainId) && record.Report.ReceivedUtc >= cutoffUtc)
            .GroupBy(record => record.Report.DomainId)
            .Select(grouping => new
            {
                DomainId = grouping.Key,
                Total = grouping.Sum(record => (long)record.MessageCount),
                Passing = grouping.Sum(record => record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass ? (long)record.MessageCount : 0L),
            })
            .ToListAsync(cancellationToken);
        return totals.Where(total => total.Total > 0).ToDictionary(total => total.DomainId, total => (double)total.Passing / total.Total);
    }

    private static async Task<Results<Ok<ApiPage<ApiDomain>>, ValidationProblem>> ListDomainsAsync(
        ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider, int? page, int? pageSize, int? group, int? tag,
        bool? monitored, CancellationToken cancellationToken)
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
            // A group outside the key's scope matches nothing, rather than revealing which visible domains are in it.
            query = scope.Includes(groupId)
                ? query.Where(domain => domain.Groups.Any(candidate => candidate.Id == groupId))
                : query.Where(_ => false);
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
        var domains = await query
            .OrderBy(domain => domain.SortOrder)
            .ThenBy(domain => domain.Name)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Include(domain => domain.Groups)
            .Include(domain => domain.Tags)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var passRates = await PassRatesAsync(context, domains.Select(domain => domain.Id).ToList(),
            DomainStatistics.GetWindowCutoffUtc(timeProvider.GetUtcNow()), cancellationToken);
        var items = domains.Select(domain => ApiDomain.From(domain, scope, PassRateOf(passRates, domain.Id))).ToList();
        return TypedResults.Ok(new ApiPage<ApiDomain>(items, pageNumber, size, totalCount));
    }

    private static async Task<Results<Ok<ApiDomainDetail>, ProblemHttpResult>> GetDomainAsync(
        [Microsoft.AspNetCore.Mvc.FromRoute(Name = "domain")] string domainKey, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var found = await LoadForApiAsync(context, scope, domainKey, cancellationToken);
        if (found is null)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var passRates = await PassRatesAsync(context, [found.Id], DomainStatistics.GetWindowCutoffUtc(timeProvider.GetUtcNow()), cancellationToken);
        return TypedResults.Ok(ApiDomainDetail.From(found, scope, PassRateOf(passRates, found.Id)));
    }

    private static async Task<Results<Ok<ApiReportSummary>, ValidationProblem, ProblemHttpResult>> GetReportSummaryAsync(
        [Microsoft.AspNetCore.Mvc.FromRoute(Name = "domain")] string domainKey, int? days, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var maximumDays = (int)DomainStatistics.ReportWindow.TotalDays;
        var windowDays = days ?? maximumDays;
        if (windowDays < 1 || windowDays > maximumDays)
        {
            return ApiProblems.Validation("days", $"Must be between 1 and {maximumDays}.");
        }

        var cutoffUtc = timeProvider.GetUtcNow().AddDays(-windowDays);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await ApiDomainKey.Matching(ApiScope.From(user).Domains(context.Domains.AsNoTracking()), domainKey)
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
            return ApiProblems.NotFound($"domain {domainKey}");
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

    private static double? PassRateOf(Dictionary<int, double> passRates, int domainId) =>
        passRates.TryGetValue(domainId, out var passRate) ? passRate : null;
}
