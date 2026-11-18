using System.Security.Claims;
using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Reporting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace DotMarc.Api;

public static class SourceEndpoints
{
    private const int DefaultPageSize = 50;

    public sealed class SourceFilters
    {
        [FromQuery(Name = "days")] public string? Days { get; set; }
        [FromQuery(Name = "from")] public string? From { get; set; }
        [FromQuery(Name = "to")] public string? To { get; set; }
        [FromQuery(Name = "result")] public string? Result { get; set; }
        [FromQuery(Name = "spf")] public string? Spf { get; set; }
        [FromQuery(Name = "dkim")] public string? Dkim { get; set; }
        [FromQuery(Name = "disposition")] public string[]? Disposition { get; set; }
        [FromQuery(Name = "minVolume")] public string? MinVolume { get; set; }
        [FromQuery(Name = "ip")] public string? Ip { get; set; }
        [FromQuery(Name = "organization")] public string? Organization { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    private static readonly IReadOnlyList<string> Results = ["passing", "failing"];
    private static readonly IReadOnlyList<string> PassOrFail = ["Pass", "Fail"];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/domains/{domain}/sources", ListSourcesAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("ListSources")
            .WithSummary("List a domain's sending sources")
            .WithDescription($"Every IP that sent as the domain over the range, busiest first, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. Filters combine. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("days", "The last 1 to 30 days (default 30). Use this or from and to.", type: JsonSchemaType.Integer)
            .DocumentsFilter("from", "Start of the range: a date (2026-10-01) or time with a zone, included. At most 366 days before to.")
            .DocumentsFilter("to", "End of the range: a date or time, not included. Defaults to now.")
            .DocumentsFilter("result", "passing for sources with no failed messages, failing for those with any.", Results)
            .DocumentsFilter("spf", "Pass for sources whose SPF passed at least once, Fail for the rest.", PassOrFail)
            .DocumentsFilter("dkim", "Pass for sources whose DKIM passed at least once, Fail for the rest.", PassOrFail)
            .DocumentsFilter("disposition", "The most severe action receivers took." + ApiFilterDocs.ListHint, Enum.GetNames<DispositionResult>())
            .DocumentsFilter("minVolume", "At least this many messages.", type: JsonSchemaType.Integer)
            .DocumentsFilter("ip", "Part of the IP address.")
            .DocumentsFilter("organization", "Part of the network owner's name, in any letter case. IPs dotMARC hasn't looked up yet never match.")
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);
    }

    private static async Task<Results<Ok<ApiPage<ApiSourceDetail>>, ValidationProblem, ProblemHttpResult>> ListSourcesAsync(
        [FromRoute(Name = "domain")] string domainKey, [AsParameters] SourceFilters filters, ClaimsPrincipal user,
        IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var (startUtc, endUtc) = reader.ReportRange(filters.Days, filters.From, filters.To, timeProvider.GetUtcNow());
        var result = reader.Single("result", filters.Result, Results);
        var spf = reader.Single("spf", filters.Spf, PassOrFail);
        var dkim = reader.Single("dkim", filters.Dkim, PassOrFail);
        var dispositions = reader.Enum<DispositionResult>("disposition", filters.Disposition);
        var minVolume = reader.Integer("minVolume", filters.MinVolume, 1, int.MaxValue);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainId = await ApiDomainKey.Matching(ApiScope.From(user).Domains(context.Domains.AsNoTracking()), domainKey)
            .Select(domain => (int?)domain.Id).SingleOrDefaultAsync(cancellationToken);
        if (domainId is null)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var records = context.ReportRecords.AsNoTracking()
            .Where(record => record.Report.DomainId == domainId && record.Report.ReceivedUtc >= startUtc && record.Report.ReceivedUtc < endUtc);
        if (LikePattern.Contains(filters.Ip) is { } ipPattern)
        {
            records = records.Where(record => EF.Functions.ILike(record.SourceIp, ipPattern, LikePattern.Escape));
        }

        var sources = records
            .GroupBy(record => record.SourceIp)
            .Select(grouping => new SourceTotals
            {
                Ip = grouping.Key,
                Volume = grouping.Sum(record => record.MessageCount),
                Passed = grouping.Sum(record => record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass ? record.MessageCount : 0),
                SpfPasses = grouping.Sum(record => record.SpfResult == AuthResult.Pass ? 1 : 0),
                DkimPasses = grouping.Sum(record => record.DkimResult == AuthResult.Pass ? 1 : 0),
                Rejected = grouping.Sum(record => record.Disposition == DispositionResult.Reject ? 1 : 0),
                Quarantined = grouping.Sum(record => record.Disposition == DispositionResult.Quarantine ? 1 : 0),
            });
        if (result == "passing") sources = sources.Where(source => source.Passed == source.Volume);
        if (result == "failing") sources = sources.Where(source => source.Passed < source.Volume);
        if (spf is not null) sources = spf == "Pass" ? sources.Where(source => source.SpfPasses > 0) : sources.Where(source => source.SpfPasses == 0);
        if (dkim is not null) sources = dkim == "Pass" ? sources.Where(source => source.DkimPasses > 0) : sources.Where(source => source.DkimPasses == 0);
        if (dispositions.Count > 0)
        {
            var reject = dispositions.Contains(DispositionResult.Reject);
            var quarantine = dispositions.Contains(DispositionResult.Quarantine);
            var none = dispositions.Contains(DispositionResult.None);
            sources = sources.Where(source => (reject && source.Rejected > 0)
                || (quarantine && source.Rejected == 0 && source.Quarantined > 0)
                || (none && source.Rejected == 0 && source.Quarantined == 0));
        }

        if (minVolume is { } atLeast) sources = sources.Where(source => source.Volume >= atLeast);
        var ordered = sources.OrderByDescending(source => source.Volume).ThenBy(source => source.Ip);

        // An organization filter matches cached names, so it filters after the totals; otherwise page in the database.
        List<SourceTotals> pageRows;
        int totalCount;
        Func<string, (string? Organization, string? Country)> owner;
        if (LikePattern.Contains(filters.Organization) is not null)
        {
            var all = await ordered.ToListAsync(cancellationToken);
            owner = await OwnersAsync(context, all.Select(source => source.Ip).ToList(), timeProvider, cancellationToken);
            var needle = filters.Organization!.Trim();
            var matching = all.Where(source => owner(source.Ip).Organization?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true).ToList();
            totalCount = matching.Count;
            pageRows = matching.Skip((pageNumber - 1) * size).Take(size).ToList();
        }
        else
        {
            totalCount = await ordered.CountAsync(cancellationToken);
            pageRows = await ordered.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
            owner = await OwnersAsync(context, pageRows.Select(source => source.Ip).ToList(), timeProvider, cancellationToken);
        }

        // The page was totalled in SQL, so the sending service is worked out from just this page's IPs and their auth details.
        var pageIps = pageRows.Select(source => source.Ip).ToList();
        var pageRecords = await records.Where(record => pageIps.Contains(record.SourceIp)).Include(record => record.AuthDetails).ToListAsync(cancellationToken);
        var ownerByIp = pageIps.ToDictionary(ip => ip, ip => owner(ip).Organization);
        var serviceByIp = SenderIdentification.IdentifyRecords(pageRecords, ownerByIp, []).ServiceByIp;

        var items = pageRows.Select(source =>
        {
            var (organization, country) = owner(source.Ip);
            return new ApiSourceDetail(source.Ip, source.Volume, source.Passed, source.Volume - source.Passed,
                source.Volume == 0 ? null : (double)source.Passed / source.Volume,
                source.SpfPasses > 0 ? "Pass" : "Fail", source.DkimPasses > 0 ? "Pass" : "Fail",
                (source.Rejected > 0 ? DispositionResult.Reject : source.Quarantined > 0 ? DispositionResult.Quarantine : DispositionResult.None).ToString(),
                organization, country, serviceByIp.GetValueOrDefault(source.Ip));
        }).ToList();
        return TypedResults.Ok(new ApiPage<ApiSourceDetail>(items, pageNumber, size, totalCount));
    }

    /// <summary>The network owner for each IP from dotMARC's lookup cache, as the domain page shows it: the IP's own
    /// result while it's current, else a cached range containing it, else unknown. Never looks anything up.</summary>
    private static async Task<Func<string, (string? Organization, string? Country)>> OwnersAsync(DotMarcDbContext context, List<string> ips,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var cached = await IpInfoService.GetCachedAsync(context, ips, cancellationToken);
        var ranges = await IpInfoService.GetCachedRangesAsync(context, cancellationToken);
        var nowUtc = timeProvider.GetUtcNow();
        return ip =>
        {
            if (cached.TryGetValue(ip, out var info) && !IpInfoService.NeedsLookup(info, nowUtc))
            {
                return (info.Organization, info.Country);
            }

            return IpRangeMatcher.FindContaining(ranges, ip) is { } range ? (range.Organization, range.Country) : (null, null);
        };
    }

    private sealed class SourceTotals
    {
        public string Ip { get; init; } = "";
        public int Volume { get; init; }
        public int Passed { get; init; }
        public int SpfPasses { get; init; }
        public int DkimPasses { get; init; }
        public int Rejected { get; init; }
        public int Quarantined { get; init; }
    }
}
