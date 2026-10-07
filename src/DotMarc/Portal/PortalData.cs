using System.Globalization;
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Reporting;
using DotMarc.Security;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

public sealed record PortalDomainSummary(string Name, PortalDomainStatus Status, double? PassRate, IReadOnlyList<double?> Trend, IReadOnlyList<AlertEvent> OpenAlerts);

public sealed record PortalDomainDetail(PortalDomainSummary Summary, Domain Domain, IReadOnlyList<SourceAggregate> TopSources, IReadOnlyList<AlertEvent> RecentAlerts);

/// <summary>Loads what the portal shows, always limited to the given Groups. An empty set of Groups shows nothing: unlike
/// elsewhere in dotMARC, "no Groups" never means "every domain" here.</summary>
public sealed class PortalData(IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider)
{
    private const int TrendDays = 30;
    private const int TopSourceCount = 10;

    public static IReadOnlyCollection<int> ScopedGroupIds(ClaimsPrincipal user) =>
        user.FindAll(UserAccessClaimsTransformation.ScopedGroupClaimType)
            .Select(claim => int.Parse(claim.Value, CultureInfo.InvariantCulture))
            .ToHashSet();

    public async Task<IReadOnlyList<PortalDomainSummary>> ListDomainsAsync(IReadOnlyCollection<int> groupIds, CancellationToken cancellationToken = default)
    {
        if (groupIds.Count == 0)
        {
            return [];
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var domains = await DomainsWithReports(context, groupIds).ToListAsync(cancellationToken).ConfigureAwait(false);
        var openAlerts = await OpenAlertsAsync(context, domains.Select(domain => domain.Name).ToList(), cancellationToken).ConfigureAwait(false);
        return domains
            .Select(domain => Summarise(domain, openAlerts[domain.Name]))
            .OrderBy(summary => summary.Status.Health)
            .ThenBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PortalDomainDetail?> GetDomainAsync(IReadOnlyCollection<int> groupIds, string domainName, CancellationToken cancellationToken = default)
    {
        if (groupIds.Count == 0)
        {
            return null;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var domain = await DomainsWithReports(context, groupIds).SingleOrDefaultAsync(candidate => candidate.Name == domainName, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            return null;
        }

        var openAlerts = await OpenAlertsAsync(context, [domain.Name], cancellationToken).ConfigureAwait(false);
        var recentCutoff = timeProvider.GetUtcNow().AddDays(-30);
        var recentAlerts = await context.AlertEvents.AsNoTracking()
            .Where(alert => alert.DomainName == domain.Name && (!alert.IsResolved || alert.ResolvedUtc >= recentCutoff))
            .OrderByDescending(alert => alert.CreatedUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var topSources = DomainStatistics.GetSourceAggregates(domain.Reports)
            .OrderByDescending(source => source.Volume)
            .Take(TopSourceCount)
            .ToList();
        return new PortalDomainDetail(Summarise(domain, openAlerts[domain.Name]), domain, topSources, recentAlerts);
    }

    private IQueryable<Domain> DomainsWithReports(DotMarcDbContext context, IReadOnlyCollection<int> groupIds)
    {
        var cutoff = DomainStatistics.GetWindowCutoffUtc(timeProvider.GetUtcNow());
        return context.Domains.AsNoTracking()
            .Where(domain => domain.IsMonitored && domain.Groups.Any(group => groupIds.Contains(group.Id)))
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.OverrideReasons)
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.AuthDetails)
            .AsSplitQuery();
    }

    private static async Task<ILookup<string, AlertEvent>> OpenAlertsAsync(DotMarcDbContext context, IReadOnlyList<string> domainNames, CancellationToken cancellationToken) =>
        (await context.AlertEvents.AsNoTracking()
            .Where(alert => !alert.IsResolved && domainNames.Contains(alert.DomainName))
            .OrderByDescending(alert => alert.CreatedUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .ToLookup(alert => alert.DomainName);

    private PortalDomainSummary Summarise(Domain domain, IEnumerable<AlertEvent> openAlerts)
    {
        var alerts = openAlerts.ToList();
        return new PortalDomainSummary(
            domain.Name,
            PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, alerts),
            DomainStatistics.GetPassRate(domain.Reports),
            PortalTrend.DailyPassRates(domain.Reports, TrendDays, timeProvider.GetUtcNow()),
            alerts);
    }
}
