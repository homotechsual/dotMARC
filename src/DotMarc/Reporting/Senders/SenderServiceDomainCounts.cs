using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.IpEnrichment;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting;

/// <summary>How many domains show one sending service: <see cref="Found"/> where the evidence for it is strong, and
/// <see cref="Likely"/> where it is attributed on weaker evidence (the "moderate" and "weak" bands of the Sources table).</summary>
public sealed record ServiceDomainCount(int Found, int Likely);

/// <summary>Counts, for each sending service, the monitored domains whose last 30 days of reports show it. The baseline and group
/// defaults screens use it so someone deciding on a service can see how many domains the decision touches.</summary>
public static class SenderServiceDomainCounts
{
    /// <summary>How many domains to load reports for at once, so a large estate does not sit in memory together.</summary>
    private const int DomainsPerBatch = 50;

    /// <summary>Counts each service once per domain, from the identification of every domain. The unrecognised row is not a service.</summary>
    public static IReadOnlyDictionary<string, ServiceDomainCount> Count(IEnumerable<SenderIdentificationResult> perDomain)
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var likely = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in perDomain)
        {
            foreach (var row in result.Services.Where(row => !row.IsUnrecognised))
            {
                var bucket = row.Evidence >= SenderScoring.StrongFrom ? found : likely;
                bucket[row.Name] = bucket.GetValueOrDefault(row.Name) + 1;
            }
        }

        return found.Keys.Concat(likely.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, name => new ServiceDomainCount(found.GetValueOrDefault(name), likely.GetValueOrDefault(name)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Identifies the senders of every monitored domain (or of the monitored domains in one group) the way the alerts do, from
    /// the cached IP owners and the domain's stored SPF ranges, and counts them.</summary>
    public static async Task<IReadOnlyDictionary<string, ServiceDomainCount>> LoadAsync(DotMarcDbContext db, int? groupId, CancellationToken cancellationToken)
    {
        var domainIds = await db.Domains.AsNoTracking()
            .Where(domain => domain.IsMonitored && (groupId == null || domain.Groups.Any(group => group.Id == groupId)))
            .OrderBy(domain => domain.Id)
            .Select(domain => domain.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ranges = await IpInfoService.GetCachedRangesAsync(db, cancellationToken).ConfigureAwait(false);
        var windowStartUtc = DomainStatistics.GetWindowCutoffUtc();
        var nowUtc = DateTimeOffset.UtcNow;

        var results = new List<SenderIdentificationResult>();
        foreach (var batch in domainIds.Chunk(DomainsPerBatch))
        {
            var domains = await db.Domains.AsNoTracking()
                .Where(domain => batch.Contains(domain.Id))
                .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= windowStartUtc))
                .ThenInclude(report => report.Records)
                .ThenInclude(record => record.AuthDetails)
                .AsSplitQuery()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var ips = domains.SelectMany(domain => domain.Reports).SelectMany(report => report.Records).Select(record => record.SourceIp).Distinct().ToList();
            var cached = ips.Count == 0 ? [] : await IpInfoService.GetCachedAsync(db, ips, cancellationToken).ConfigureAwait(false);

            foreach (var domain in domains)
            {
                var domainIps = domain.Reports.SelectMany(report => report.Records).Select(record => record.SourceIp).Distinct();
                var ownerByIp = IpInfoService.OwnerByIp(cached, ranges, domainIps, nowUtc);
                results.Add(SenderIdentification.Identify(domain.Reports, ownerByIp, [], SpfServiceRanges.Parse(domain.SpfRangesJson)));
            }
        }

        return Count(results);
    }
}
