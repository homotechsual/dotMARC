using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;

namespace DotMarc.Reporting.ClientReports;

public sealed record ClientReportInputs(
    ResolvedBrand Brand, byte[]? Logo, string GroupName, ReportPeriod Period, TimeZoneInfo Zone, IReadOnlyList<Domain> Domains,
    IReadOnlyList<Report> Reports, IReadOnlyList<AlertEvent> PeriodAlerts, IReadOnlyList<AlertEvent> OpenAlerts,
    IReadOnlyDictionary<string, IpInfo> Owners, DateTimeOffset NowUtc);

/// <summary>Turns loaded rows into a report. Pure, so every figure can be tested without a database. A report belongs to
/// the local day its date range begins, the same rule as the portal's trend, measured in the report time zone.</summary>
public static class ClientReportCalculator
{
    public const int TopSenderCount = 10;
    private const int DailyTrendLimitDays = 92;

    public static ClientReport Build(ClientReportInputs inputs)
    {
        var comparison = ReportPeriods.PreviousForComparison(inputs.Period);
        var byDomain = inputs.Reports.ToLookup(report => report.DomainId);
        var domains = inputs.Domains
            .OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase)
            .Select(domain =>
            {
                var reports = byDomain[domain.Id].ToList();
                var inPeriod = reports.Where(report => Covers(inputs.Period, report, inputs.Zone)).ToList();
                var inComparison = reports.Where(report => Covers(comparison, report, inputs.Zone)).ToList();
                var openAlerts = inputs.OpenAlerts.Where(alert => alert.DomainName == domain.Name).ToList();
                return new ClientReportDomain(
                    domain.Name,
                    PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, openAlerts),
                    inPeriod.SelectMany(report => report.Records).Sum(record => (long)record.MessageCount),
                    DomainStatistics.GetPassRate(inPeriod),
                    DomainStatistics.GetPassRate(inComparison),
                    Trend(inputs.Period, inPeriod, inputs.Zone),
                    PortalWording.PolicySentence(domain),
                    PortalWording.HealthRows(domain),
                    TopSenders(inPeriod, inputs.Owners),
                    Receivers(inPeriod),
                    Alerts(inputs.PeriodAlerts.Where(alert => alert.DomainName == domain.Name)));
            })
            .ToList();

        return new ClientReport(
            inputs.Brand, inputs.Logo, inputs.GroupName, inputs.Period, inputs.Zone.Id,
            domains.Count == 0 ? "There are no domains in this report." : PortalStatus.Verdict(domains.Select(domain => domain.Status).ToList()),
            domains, ClientReportNextSteps.For(inputs.Domains.OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase).ToList(), domains),
            inputs.NowUtc);
    }

    /// <summary>An alert left open is raised again after each cooldown as a new row, so copies are folded into one line:
    /// when it was first raised, and when it was resolved (or still open, if any copy is).</summary>
    private static IReadOnlyList<ClientReportAlert> Alerts(IEnumerable<AlertEvent> alerts) =>
        alerts
            .GroupBy(alert => (alert.AlertType, alert.Title))
            .Select(copies => new ClientReportAlert(
                copies.Key.Title,
                copies.Min(alert => alert.CreatedUtc),
                copies.Any(alert => !alert.IsResolved) ? null : copies.Max(alert => alert.ResolvedUtc)))
            .OrderBy(alert => alert.CreatedUtc)
            .ToList();

    private static bool Covers(ReportPeriod period, Report report, TimeZoneInfo zone)
    {
        var day = ReportPeriods.LocalDay(report.DateRangeBeginUtc, zone);
        return day >= period.Start && day <= period.End;
    }

    /// <summary>A pass rate per local day, or per 7-day bucket from the start for periods over 92 days; null where no
    /// mail was reported.</summary>
    private static IReadOnlyList<double?> Trend(ReportPeriod period, IReadOnlyList<Report> reports, TimeZoneInfo zone)
    {
        var bucketDays = period.Days > DailyTrendLimitDays ? 7 : 1;
        var buckets = (period.Days + bucketDays - 1) / bucketDays;
        var byBucket = reports.ToLookup(report => (ReportPeriods.LocalDay(report.DateRangeBeginUtc, zone).DayNumber - period.Start.DayNumber) / bucketDays);
        return Enumerable.Range(0, buckets).Select(bucket => DomainStatistics.GetPassRate(byBucket[bucket])).ToList();
    }

    private static IReadOnlyList<ClientReportSender> TopSenders(IReadOnlyList<Report> reports, IReadOnlyDictionary<string, IpInfo> owners)
    {
        var records = reports.SelectMany(report => report.Records).ToList();
        var total = records.Sum(record => (long)record.MessageCount);
        return records
            .GroupBy(record => record.SourceIp)
            .Select(source =>
            {
                var messages = source.Sum(record => (long)record.MessageCount);
                var passing = source.Where(DomainStatistics.IsPassing).Sum(record => (long)record.MessageCount);
                return new ClientReportSender(source.Key, owners.TryGetValue(source.Key, out var owner) ? owner.Organization : null,
                    messages, passing, messages - passing, total == 0 ? 0 : (double)messages / total);
            })
            .OrderByDescending(sender => sender.Messages)
            .ThenBy(sender => sender.Ip, StringComparer.Ordinal)
            .Take(TopSenderCount)
            .ToList();
    }

    private static ReceiverActions Receivers(IReadOnlyList<Report> reports)
    {
        var records = reports.SelectMany(report => report.Records).ToList();
        long Sum(Func<ReportRecord, bool> predicate) => records.Where(predicate).Sum(record => (long)record.MessageCount);
        return new ReceiverActions(
            Sum(record => record.Disposition == DispositionResult.None),
            Sum(record => record.Disposition == DispositionResult.Quarantine),
            Sum(record => record.Disposition == DispositionResult.Reject),
            Sum(record => record.Disposition == DispositionResult.None && !DomainStatistics.IsPassing(record)));
    }
}
