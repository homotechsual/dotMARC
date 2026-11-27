using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using DotMarc.Reporting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class DomainReadEndpoints
{
    public const int MaximumPageSize = 200;
    private const int DefaultPageSize = 50;
    private const int TopSourceCount = 20;

    /// <summary>The domain list's filters; see the spec's section 3.</summary>
    public sealed class DomainFilters
    {
        [FromQuery(Name = "search")] public string? Search { get; set; }
        [FromQuery(Name = "group")] public string[]? Group { get; set; }
        [FromQuery(Name = "tag")] public string[]? Tag { get; set; }
        [FromQuery(Name = "monitored")] public string? Monitored { get; set; }
        [FromQuery(Name = "dmarc")] public string[]? Dmarc { get; set; }
        [FromQuery(Name = "spf")] public string[]? Spf { get; set; }
        [FromQuery(Name = "dkim")] public string[]? Dkim { get; set; }
        [FromQuery(Name = "mx")] public string[]? Mx { get; set; }
        [FromQuery(Name = "tlsrpt")] public string[]? Tlsrpt { get; set; }
        [FromQuery(Name = "mtaSts")] public string[]? MtaSts { get; set; }
        [FromQuery(Name = "dmarcAuthorization")] public string[]? DmarcAuthorization { get; set; }
        [FromQuery(Name = "status")] public string[]? Status { get; set; }
        [FromQuery(Name = "policy")] public string[]? Policy { get; set; }
        [FromQuery(Name = "dnsProvider")] public string[]? DnsProvider { get; set; }
        [FromQuery(Name = "dmarcTarget")] public string[]? DmarcTarget { get; set; }
        [FromQuery(Name = "lastReportAfter")] public string? LastReportAfter { get; set; }
        [FromQuery(Name = "lastReportBefore")] public string? LastReportBefore { get; set; }
        [FromQuery(Name = "noReports")] public string? NoReports { get; set; }
        [FromQuery(Name = "passRateBelow")] public string? PassRateBelow { get; set; }
        [FromQuery(Name = "passRateAtLeast")] public string? PassRateAtLeast { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/domains", ListDomainsAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Domains)
            .WithName("ListDomains")
            .WithSummary("List domains")
            .WithDescription($"Domains this key can see, in dashboard order, {DefaultPageSize} a page by default and at most {MaximumPageSize}. Filters combine: a domain must match every filter given. The pass rate covers the last 30 days and is null with no reports.")
            .DocumentsFilter("search", "Part of the domain name, in any letter case.")
            .DocumentsFilter("group", "One or more group ids." + ApiFilterDocs.ListHint, type: JsonSchemaType.Integer)
            .DocumentsFilter("tag", "One or more tag ids." + ApiFilterDocs.ListHint, type: JsonSchemaType.Integer)
            .DocumentsFilter("monitored", "true for domains dotMARC monitors, false for the rest.", type: JsonSchemaType.Boolean)
            .DocumentsFilter("dmarc", "One or more DMARC check results." + ApiFilterDocs.ListHint, Enum.GetNames<DmarcCheckStatus>())
            .DocumentsFilter("spf", "One or more SPF check results." + ApiFilterDocs.ListHint, Enum.GetNames<SpfCheckStatus>())
            .DocumentsFilter("dkim", "One or more DKIM check results." + ApiFilterDocs.ListHint, Enum.GetNames<DkimCheckStatus>())
            .DocumentsFilter("mx", "One or more MX check results." + ApiFilterDocs.ListHint, Enum.GetNames<MxCheckStatus>())
            .DocumentsFilter("tlsrpt", "One or more TLS reporting check results." + ApiFilterDocs.ListHint, Enum.GetNames<TlsrptCheckStatus>())
            .DocumentsFilter("mtaSts", "One or more MTA-STS states." + ApiFilterDocs.ListHint, Enum.GetNames<MtaStsStatus>())
            .DocumentsFilter("dmarcAuthorization", "One or more DMARC authorization check results." + ApiFilterDocs.ListHint, Enum.GetNames<DmarcAuthorizationCheckStatus>())
            .DocumentsFilter("status", "One or more plain-language statuses, worked out as the client portal does." + ApiFilterDocs.ListHint, Enum.GetNames<PortalHealth>())
            .DocumentsFilter("policy", "One or more published DMARC policies." + ApiFilterDocs.ListHint, Enum.GetNames<DmarcPolicyLevel>())
            .DocumentsFilter("dnsProvider", "One or more DNS providers." + ApiFilterDocs.ListHint, Enum.GetNames<DetectedDnsProvider>())
            .DocumentsFilter("dmarcTarget", "One or more outcomes against the DMARC target that applies to the domain: meets, below, or checking (not yet checked). Only monitored domains with a target match, as on the Baselines page." + ApiFilterDocs.ListHint, ApiDmarcTarget.OutcomeNames)
            .DocumentsFilter("lastReportAfter", "Last report received at or after this date or time. Domains with no report never match.")
            .DocumentsFilter("lastReportBefore", "Last report received before this date or time. Domains with no report never match.")
            .DocumentsFilter("noReports", "true for domains that have never had a report, false for those that have.", type: JsonSchemaType.Boolean)
            .DocumentsFilter("passRateBelow", "30-day pass rate below this, from 0 to 1. Domains with no pass rate never match.", type: JsonSchemaType.Number)
            .DocumentsFilter("passRateAtLeast", "30-day pass rate at least this, from 0 to 1. Domains with no pass rate never match.", type: JsonSchemaType.Number)
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);

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
            .WithDescription($"Volume, pass rate, why failing mail was let through or rejected, and the busiest sending IPs (20 by default), over the last 1 to 30 days (default 30), or any range of up to 366 days with from and to. {ApiDomainKey.Description}")
            .DocumentsFilter("days", "The last 1 to 30 days (default 30). Use this or from and to.", type: JsonSchemaType.Integer)
            .DocumentsFilter("from", "Start of the range: a date (2026-10-01) or time with a zone, included. At most 366 days before to.")
            .DocumentsFilter("to", "End of the range: a date or time, not included. Defaults to now.")
            .DocumentsFilter("top", "How many of the busiest sending IPs to return, 1 to 200 (default 20).", type: JsonSchemaType.Integer)
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
        [AsParameters] DomainFilters filters, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var groupIds = reader.Ids("group", filters.Group);
        var tagIds = reader.Ids("tag", filters.Tag);
        var dmarc = reader.Enum<DmarcCheckStatus>("dmarc", filters.Dmarc);
        var spf = reader.Enum<SpfCheckStatus>("spf", filters.Spf);
        var dkim = reader.Enum<DkimCheckStatus>("dkim", filters.Dkim);
        var mx = reader.Enum<MxCheckStatus>("mx", filters.Mx);
        var tlsrpt = reader.Enum<TlsrptCheckStatus>("tlsrpt", filters.Tlsrpt);
        var mtaSts = reader.Enum<MtaStsStatus>("mtaSts", filters.MtaSts);
        var dmarcAuthorization = reader.Enum<DmarcAuthorizationCheckStatus>("dmarcAuthorization", filters.DmarcAuthorization);
        var statuses = reader.Enum<PortalHealth>("status", filters.Status);
        var policies = reader.Enum<DmarcPolicyLevel>("policy", filters.Policy);
        var providers = reader.Enum<DetectedDnsProvider>("dnsProvider", filters.DnsProvider);
        var dmarcTargets = reader.OneOf("dmarcTarget", filters.DmarcTarget, ApiDmarcTarget.OutcomeNames);
        var lastReportAfter = reader.Date("lastReportAfter", filters.LastReportAfter);
        var lastReportBefore = reader.Date("lastReportBefore", filters.LastReportBefore);
        reader.Range("lastReportAfter", lastReportAfter, "lastReportBefore", lastReportBefore);
        var monitored = reader.Bool("monitored", filters.Monitored);
        var noReports = reader.Bool("noReports", filters.NoReports);
        var passRateBelow = reader.Fraction("passRateBelow", filters.PassRateBelow);
        var passRateAtLeast = reader.Fraction("passRateAtLeast", filters.PassRateAtLeast);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = scope.Domains(context.Domains.AsNoTracking());
        if (LikePattern.Contains(filters.Search) is { } searchPattern)
        {
            query = query.Where(domain => EF.Functions.ILike(domain.Name, searchPattern, LikePattern.Escape));
        }

        if (groupIds.Count > 0)
        {
            // Groups outside the key's scope count for nothing, rather than revealing which visible domains are in them.
            var inScope = groupIds.Where(scope.Includes).ToList();
            query = query.Where(domain => domain.Groups.Any(group => inScope.Contains(group.Id)));
        }

        if (tagIds.Count > 0)
        {
            query = query.Where(domain => domain.Tags.Any(tag => tagIds.Contains(tag.Id)));
        }

        if (monitored is { } isMonitored)
        {
            query = query.Where(domain => domain.IsMonitored == isMonitored);
        }

        if (dmarc.Count > 0) query = query.Where(domain => dmarc.Contains(domain.DmarcCheckStatus));
        if (spf.Count > 0) query = query.Where(domain => spf.Contains(domain.SpfCheckStatus));
        if (dkim.Count > 0) query = query.Where(domain => dkim.Contains(domain.DkimCheckStatus));
        if (mx.Count > 0) query = query.Where(domain => mx.Contains(domain.MxCheckStatus));
        if (tlsrpt.Count > 0) query = query.Where(domain => tlsrpt.Contains(domain.TlsrptCheckStatus));
        if (mtaSts.Count > 0) query = query.Where(domain => mtaSts.Contains(domain.MtaStsStatus));
        if (dmarcAuthorization.Count > 0) query = query.Where(domain => dmarcAuthorization.Contains(domain.DmarcAuthorizationCheckStatus));
        if (policies.Count > 0) query = query.Where(domain => domain.DmarcPolicy != null && policies.Contains(domain.DmarcPolicy.Value));
        if (providers.Count > 0) query = query.Where(domain => providers.Contains(domain.DnsProvider));
        if (lastReportAfter is { } reportFrom) query = query.Where(domain => domain.LastReportReceivedUtc != null && domain.LastReportReceivedUtc >= reportFrom);
        if (lastReportBefore is { } reportUntil) query = query.Where(domain => domain.LastReportReceivedUtc != null && domain.LastReportReceivedUtc < reportUntil);
        if (noReports is { } neverReported) query = query.Where(domain => (domain.LastReportReceivedUtc == null) == neverReported);

        var ordered = query.OrderBy(domain => domain.SortOrder).ThenBy(domain => domain.Name);
        var cutoffUtc = DomainStatistics.GetWindowCutoffUtc(timeProvider.GetUtcNow());
        int totalCount;
        List<int> pageIds;
        if (statuses.Count == 0 && dmarcTargets.Count == 0 && passRateBelow is null && passRateAtLeast is null)
        {
            totalCount = await ordered.CountAsync(cancellationToken);
            pageIds = await ordered.Skip((pageNumber - 1) * size).Take(size).Select(domain => domain.Id).ToListAsync(cancellationToken);
        }
        else
        {
            // The status, pass rate and DMARC target are worked out outside the query, so filter every candidate here, then page.
            var candidates = await ordered.Include(domain => domain.Groups).ToListAsync(cancellationToken);
            var targetSettings = dmarcTargets.Count > 0 ? await DnsRecordSettingsService.GetAsync(context, cancellationToken) : null;
            // Each is worked out only when a filter needs it.
            var candidateRates = passRateBelow is not null || passRateAtLeast is not null
                ? await PassRatesAsync(context, candidates.Select(domain => domain.Id).ToList(), cutoffUtc, cancellationToken)
                : [];
            var openAlerts = statuses.Count > 0
                ? await PortalData.OpenAlertsAsync(context, candidates.Select(domain => domain.Name).ToList(), cancellationToken)
                : null;
            var matching = candidates.Where(domain =>
            {
                var rate = PassRateOf(candidateRates, domain.Id);
                return (passRateBelow is null || (rate is { } below && below < passRateBelow))
                    && (passRateAtLeast is null || (rate is { } atLeast && atLeast >= passRateAtLeast))
                    && (statuses.Count == 0 || statuses.Contains(PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, openAlerts![domain.Name].ToList()).Health))
                    && (dmarcTargets.Count == 0 || MatchesDmarcTarget(domain, targetSettings!, dmarcTargets));
            }).Select(domain => domain.Id).ToList();
            totalCount = matching.Count;
            pageIds = matching.Skip((pageNumber - 1) * size).Take(size).ToList();
        }

        var domains = (await context.Domains.AsNoTracking()
                .Where(domain => pageIds.Contains(domain.Id))
                .Include(domain => domain.Groups)
                .Include(domain => domain.Tags)
                .AsSplitQuery()
                .ToListAsync(cancellationToken))
            .OrderBy(domain => pageIds.IndexOf(domain.Id))
            .ToList();
        var passRates = await PassRatesAsync(context, pageIds, cutoffUtc, cancellationToken);
        var settings = await DnsRecordSettingsService.GetAsync(context, cancellationToken);
        var items = domains.Select(domain => ApiDomain.From(domain, scope, PassRateOf(passRates, domain.Id), ApiDmarcTarget.For(domain, settings))).ToList();
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
        var settings = await DnsRecordSettingsService.GetAsync(context, cancellationToken);
        return TypedResults.Ok(ApiDomainDetail.From(found, scope, PassRateOf(passRates, found.Id), ApiDmarcTarget.For(found, settings)));
    }

    private static async Task<Results<Ok<ApiReportSummary>, ValidationProblem, ProblemHttpResult>> GetReportSummaryAsync(
        [Microsoft.AspNetCore.Mvc.FromRoute(Name = "domain")] string domainKey, string? days, string? from, string? to, string? top, ClaimsPrincipal user,
        IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var sourceCount = reader.Integer("top", top, 1, 200) ?? TopSourceCount;
        var (startUtc, endUtc) = reader.ReportRange(days, from, to, timeProvider.GetUtcNow());
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await ApiDomainKey.Matching(ApiScope.From(user).Domains(context.Domains.AsNoTracking()), domainKey)
            .Select(candidate => new { candidate.Id, candidate.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (domain is null)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        // Totalled in the database, so a long range never loads every record. Only the records a receiver acted on
        // (quarantined or rejected) are loaded, with their details, for the reason breakdown's rules.
        var records = context.ReportRecords.AsNoTracking()
            .Where(record => record.Report.DomainId == domain.Id && record.Report.ReceivedUtc >= startUtc && record.Report.ReceivedUtc < endUtc);
        var totals = await records
            .GroupBy(record => 1)
            .Select(grouping => new
            {
                Total = grouping.Sum(record => (long)record.MessageCount),
                Passing = grouping.Sum(record => record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass ? (long)record.MessageCount : 0L),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var sourceRows = await records
            .GroupBy(record => record.SourceIp)
            .Select(grouping => new
            {
                SourceIp = grouping.Key,
                Volume = grouping.Sum(record => record.MessageCount),
                SpfPasses = grouping.Sum(record => record.SpfResult == AuthResult.Pass ? 1 : 0),
                DkimPasses = grouping.Sum(record => record.DkimResult == AuthResult.Pass ? 1 : 0),
                Rejected = grouping.Sum(record => record.Disposition == DispositionResult.Reject ? 1 : 0),
                Quarantined = grouping.Sum(record => record.Disposition == DispositionResult.Quarantine ? 1 : 0),
            })
            .OrderByDescending(source => source.Volume)
            .ThenBy(source => source.SourceIp)
            .Take(sourceCount)
            .ToListAsync(cancellationToken);
        var actedOn = await records
            .Where(record => record.Disposition != DispositionResult.None)
            .Include(record => record.OverrideReasons)
            .Include(record => record.AuthDetails)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // Combined as DomainStatistics.GetSourceAggregates does: a pass seen once counts, and the most severe disposition wins.
        var topSources = sourceRows.Select(source => new ApiSource(
                source.SourceIp,
                source.Volume,
                (source.SpfPasses > 0 ? AuthResult.Pass : AuthResult.Fail).ToString(),
                (source.DkimPasses > 0 ? AuthResult.Pass : AuthResult.Fail).ToString(),
                (source.Rejected > 0 ? DispositionResult.Reject : source.Quarantined > 0 ? DispositionResult.Quarantine : DispositionResult.None).ToString()))
            .ToList();
        var breakdown = DomainStatistics.GetReasonBreakdown(actedOn);
        var totalVolume = totals?.Total ?? 0;
        return TypedResults.Ok(new ApiReportSummary(
            domain.Id, domain.Name, (int)Math.Ceiling((endUtc - startUtc).TotalDays), (int)totalVolume,
            totalVolume == 0 ? null : (double)totals!.Passing / totalVolume,
            new ApiReasonBreakdown(breakdown.BenignOverride, breakdown.LocalPolicy, breakdown.Other, breakdown.NoReasonGiven,
                breakdown.InferredSpfFailure, breakdown.InferredDkimFailure, breakdown.InferredBothFailure),
            topSources));
    }

    /// <summary>Whether the domain is monitored and its outcome against its target is one of those asked for. Unmonitored
    /// domains never match, so the filter agrees with the counts on the Baselines page.</summary>
    private static bool MatchesDmarcTarget(Domain domain, DnsRecordSettings settings, IReadOnlyList<string> outcomes) =>
        domain.IsMonitored && ApiDmarcTarget.For(domain, settings) is { } target && outcomes.Contains(target.Outcome);

    private static double? PassRateOf(Dictionary<int, double> passRates, int domainId) =>
        passRates.TryGetValue(domainId, out var passRate) ? passRate : null;
}
