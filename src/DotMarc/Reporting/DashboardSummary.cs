using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Notifications;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Aggregate stats shown across the Dashboard's summary tiles, plus the per-domain table
/// rows derived from the same data. <see cref="Build"/> takes a fully-loaded domain list (Reports
/// already filtered to the report window by the caller's EF query) and a parse-failure count,
/// keeping this calculation testable without EF or Blazor - same "pure core, thin I/O adapter"
/// split as <see cref="DomainStatistics"/>.</summary>
public sealed record DashboardSummary(int DomainCount, double OverallPassRate, int WarningCount, int MissingCount, int ParseFailureCount, int SourceCount, ReasonBreakdown ReasonBreakdown)
{
    /// <summary>The dashboard's per-domain status: Missing when a monitored domain that sends mail has had no report for
    /// the applicable rule's interval, Warning when its pass rate is under 95%, otherwise OK. Shared with the API's summary.</summary>
    public static string StatusOf(Domain domain, double? passRate, AlertRuleSet? rules = null, DateTimeOffset? now = null)
    {
        var missingReport = MissingReportCheck.IsOverdue(domain, rules ?? AlertRuleSet.Defaults(), now ?? DateTimeOffset.UtcNow);
        return missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
    }

    /// <summary>Needs each domain's Groups loaded when <paramref name="settings"/> is given, to work out its DMARC target outcome;
    /// without settings every row has no target.</summary>
    public static (DashboardSummary Summary, List<DashboardDomainRow> Rows) Build(IReadOnlyList<Domain> domains, int parseFailureCount, DnsRecordSettings? settings = null, AlertRuleSet? rules = null, DateTimeOffset? now = null)
    {
        rules ??= AlertRuleSet.Defaults();
        now ??= DateTimeOffset.UtcNow;
        var rows = domains
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d =>
            {
                var passRate = DomainStatistics.GetPassRate(d.Reports);

                var status = StatusOf(d, passRate, rules, now);
                var color = status switch { "Missing" => Color.Error, "Warning" => Color.Warning, _ => Color.Success };
                var targetOutcome = settings is null ? DmarcTargetOutcome.NoTarget : DmarcTargetCheck.Outcome(d, DmarcTargetResolver.Resolve(d, settings));

                return new DashboardDomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored, d.DmarcCheckStatus, d.DmarcAuthorizationCheckStatus, d.MtaStsStatus, d.SpfCheckStatus, d.DkimCheckStatus, targetOutcome);
            })
            .ToList();

        var sourceCount = domains.SelectMany(d => d.Reports).Select(r => r.ReportingOrg).Distinct().Count();

        var summary = new DashboardSummary(
            rows.Count,
            DomainStatistics.GetOverallPassRate(domains.Select(d => (IEnumerable<Report>)d.Reports)),
            rows.Count(r => r.Status == "Warning"),
            rows.Count(r => r.Status == "Missing"),
            parseFailureCount,
            sourceCount,
            DomainStatistics.GetReasonBreakdown(domains.Select(d => (IEnumerable<Report>)d.Reports)));

        return (summary, rows);
    }
}

/// <summary>One domain's row in the Dashboard's table.</summary>
public sealed record DashboardDomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored, DmarcCheckStatus DmarcCheckStatus, DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus, MtaStsStatus MtaStsStatus, SpfCheckStatus SpfCheckStatus, DkimCheckStatus DkimCheckStatus, DmarcTargetOutcome TargetOutcome = DmarcTargetOutcome.NoTarget);
