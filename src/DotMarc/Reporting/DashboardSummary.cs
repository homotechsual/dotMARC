using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Aggregate stats shown across the Dashboard's summary tiles, plus the per-domain table
/// rows derived from the same data. <see cref="Build"/> takes a fully-loaded domain list (Reports
/// already filtered to the report window by the caller's EF query) and a parse-failure count,
/// keeping this calculation testable without EF or Blazor — same "pure core, thin I/O adapter"
/// split as <see cref="DomainStatistics"/>.</summary>
public sealed record DashboardSummary(int DomainCount, double OverallPassRate, int WarningCount, int MissingCount, int ParseFailureCount, int SourceCount)
{
    public static (DashboardSummary Summary, List<DashboardDomainRow> Rows) Build(IReadOnlyList<Domain> domains, int parseFailureCount)
    {
        var rows = domains
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d =>
            {
                var passRate = DomainStatistics.GetPassRate(d.Reports);

                var missingReport = d.IsMonitored && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
                var status = missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
                var color = status switch { "Missing" => Color.Error, "Warning" => Color.Warning, _ => Color.Success };

                return new DashboardDomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored, d.DmarcCheckStatus, d.MtaStsStatus);
            })
            .ToList();

        var sourceCount = domains.SelectMany(d => d.Reports).Select(r => r.ReportingOrg).Distinct().Count();

        var summary = new DashboardSummary(
            rows.Count,
            DomainStatistics.GetOverallPassRate(domains.Select(d => (IEnumerable<Report>)d.Reports)),
            rows.Count(r => r.Status == "Warning"),
            rows.Count(r => r.Status == "Missing"),
            parseFailureCount,
            sourceCount);

        return (summary, rows);
    }
}

/// <summary>One domain's row in the Dashboard's table.</summary>
public sealed record DashboardDomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored, DmarcCheckStatus DmarcCheckStatus, MtaStsStatus MtaStsStatus);
