using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Portal;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

/// <summary>Loads what a Group's report needs (its monitored domains, the period's and comparison period's reports,
/// alerts, sender owners and brand) and hands it to <see cref="ClientReportCalculator"/>.</summary>
public sealed class ClientReportBuilder(IDbContextFactory<DotMarcDbContext> dbFactory, PortalBrandLoader brandLoader, TimeProvider timeProvider)
{
    public async Task<ClientReport?> BuildAsync(int groupId, ReportPeriod period, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var group = await context.Groups.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var domains = await context.Domains.AsNoTracking()
            .Where(domain => domain.IsMonitored && domain.Groups.Any(candidate => candidate.Id == groupId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var domainIds = domains.Select(domain => domain.Id).ToList();
        var domainNames = domains.Select(domain => domain.Name).ToList();

        var periodStartUtc = ReportPeriods.StartUtc(period, zone);
        var periodEndUtc = ReportPeriods.EndUtc(period, zone);
        var loadFromUtc = ReportPeriods.StartUtc(ReportPeriods.PreviousForComparison(period), zone);
        var reports = await context.Reports.AsNoTracking()
            .Where(report => domainIds.Contains(report.DomainId) && report.DateRangeBeginUtc >= loadFromUtc && report.DateRangeBeginUtc < periodEndUtc)
            .Include(report => report.Records)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var periodAlerts = await context.AlertEvents.AsNoTracking()
            .Where(alert => domainNames.Contains(alert.DomainName)
                && ((alert.CreatedUtc >= periodStartUtc && alert.CreatedUtc < periodEndUtc) || (alert.ResolvedUtc >= periodStartUtc && alert.ResolvedUtc < periodEndUtc)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var openAlerts = await context.AlertEvents.AsNoTracking()
            .Where(alert => domainNames.Contains(alert.DomainName) && !alert.IsResolved)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ips = reports.Where(report => report.DateRangeBeginUtc >= periodStartUtc)
            .SelectMany(report => report.Records).Select(record => record.SourceIp).Distinct().ToList();
        var owners = await IpInfoService.GetCachedAsync(context, ips, cancellationToken).ConfigureAwait(false);

        var brand = await brandLoader.LoadAsync([groupId], cancellationToken).ConfigureAwait(false);
        byte[]? logo = null;
        if (brand.LogoImageId is { } logoId)
        {
            logo = await context.BrandingImages.AsNoTracking()
                .Where(image => image.Id == logoId && (image.ContentType == "image/png" || image.ContentType == "image/jpeg"))
                .Select(image => image.Bytes)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        return ClientReportCalculator.Build(new ClientReportInputs(
            brand, logo, group.Name, period, zone, domains, reports, periodAlerts, openAlerts, owners, timeProvider.GetUtcNow()));
    }
}
