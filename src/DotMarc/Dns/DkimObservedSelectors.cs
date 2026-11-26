using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Dns;

public sealed record DkimObservedSelector(string Selector, long MessageCount, DateTimeOffset LastSeenUtc);

public static class DkimObservedSelectors
{
    public static async Task<IReadOnlyList<DkimObservedSelector>> GetAsync(
        DotMarcDbContext context, int domainId, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Where(domain => domain.Id == domainId)
            .Select(domain => domain.Name).SingleAsync(cancellationToken).ConfigureAwait(false);
        var signingDomain = domain.TrimEnd('.').ToLowerInvariant();
        var observations = await context.ReportRecordAuthDetails.AsNoTracking()
            .Where(detail => detail.ReportRecord.Report.DomainId == domainId
                && detail.Mechanism == DmarcAuthMechanism.Dkim && detail.Selector != null
                && detail.Domain.Trim().TrimEnd('.').ToLower() == signingDomain)
            .Select(detail => new
            {
                Selector = detail.Selector!.Trim().ToLower(),
                detail.ReportRecordId,
                detail.ReportRecord.MessageCount,
                LastSeenUtc = detail.ReportRecord.Report.DateRangeEndUtc
            })
            // Several signatures with the same selector in one aggregate record represent the same messages.
            .Distinct()
            .GroupBy(observation => observation.Selector)
            .Select(group => new DkimObservedSelector(group.Key,
                group.Sum(observation => (long)observation.MessageCount),
                group.Max(observation => observation.LastSeenUtc)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return observations.Where(observation => IsSelector(observation.Selector, signingDomain))
            .OrderByDescending(observation => observation.MessageCount)
            .ThenBy(observation => observation.Selector, StringComparer.Ordinal).ToList();
    }

    private static bool IsSelector(string selector, string domain) =>
        $"{selector}._domainkey.{domain}".Length <= 253 && selector.Split('.').All(label =>
            label.Length is > 0 and <= 63
            && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
}
