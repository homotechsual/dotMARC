using DotMarc.Data;

namespace DotMarc.Reporting;

/// <summary>Pure statistics helpers over already-loaded <see cref="Report"/> data. Shared between
/// Dashboard.razor and DomainDetail.razor so the two pages compute the 30-day cutoff, pass rate,
/// and per-source breakdown identically instead of each carrying its own (previously
/// inconsistent) inline copy. Takes plain entity lists rather than querying the database itself,
/// matching this project's established "pure core, thin I/O adapter" split (see
/// ReportDecompressor / DmarcReportParser).</summary>
public static class DomainStatistics
{
    public static readonly TimeSpan ReportWindow = TimeSpan.FromDays(30);

    public static DateTimeOffset GetWindowCutoffUtc(DateTimeOffset? nowUtc = null) =>
        (nowUtc ?? DateTimeOffset.UtcNow) - ReportWindow;

    public static int GetTotalVolume(IEnumerable<Report> reportsInWindow) =>
        reportsInWindow.SelectMany(r => r.Records).Sum(r => r.MessageCount);

    /// <summary>Volume-weighted pass rate for a single domain's in-window reports: sum of
    /// passing message counts / sum of all message counts. Null when there's no volume in the
    /// window (nothing to rate).</summary>
    public static double? GetPassRate(IEnumerable<Report> reportsInWindow)
    {
        var records = reportsInWindow.SelectMany(r => r.Records).ToList();
        var total = records.Sum(r => r.MessageCount);
        if (total == 0)
        {
            return null;
        }

        var passing = records.Where(IsPassing).Sum(r => r.MessageCount);
        return (double)passing / total;
    }

    /// <summary>Volume-weighted pass rate across ALL supplied domains' in-window reports combined
    /// (sum of passing message counts over sum of all message counts), rather than averaging each
    /// domain's own pass rate equally — a domain sending 10 messages in the window should not move
    /// the overall figure as much as one sending 10,000.</summary>
    public static double GetOverallPassRate(IEnumerable<IEnumerable<Report>> perDomainReportsInWindow)
    {
        var allRecords = perDomainReportsInWindow.SelectMany(reports => reports.SelectMany(r => r.Records)).ToList();
        var total = allRecords.Sum(r => r.MessageCount);
        if (total == 0)
        {
            return 0;
        }

        var passing = allRecords.Where(IsPassing).Sum(r => r.MessageCount);
        return (double)passing / total;
    }

    /// <summary>Groups in-window records by source IP, summing volume and combining SPF/DKIM/
    /// disposition across that source's (possibly several) records in the window rather than
    /// taking the first one seen. Judgment call, documented here since the review flagged this as
    /// a choice without mandating a specific algorithm: SPF/DKIM use "passed at least once in the
    /// window" (optimistic, matching DMARC's own "either can pass" evaluation semantics), and
    /// disposition uses "most severe seen" (none &lt; quarantine &lt; reject) so a source that ever
    /// triggered enforcement stays visible instead of being masked by an earlier or later "none"
    /// record.</summary>
    public static List<SourceAggregate> GetSourceAggregates(IEnumerable<Report> reportsInWindow) =>
        reportsInWindow
            .SelectMany(r => r.Records)
            .GroupBy(r => r.SourceIp)
            .Select(g => new SourceAggregate(
                g.Key,
                g.Sum(r => r.MessageCount),
                CombineAuthResult(g.Select(r => r.SpfResult)),
                CombineAuthResult(g.Select(r => r.DkimResult)),
                CombineDisposition(g.Select(r => r.Disposition))))
            .ToList();

    private static bool IsPassing(ReportRecord record) =>
        record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass;

    private static AuthResult CombineAuthResult(IEnumerable<AuthResult> results) =>
        results.Any(r => r == AuthResult.Pass) ? AuthResult.Pass : AuthResult.Fail;

    private static DispositionResult CombineDisposition(IEnumerable<DispositionResult> dispositions)
    {
        var seen = dispositions.ToList();
        if (seen.Contains(DispositionResult.Reject))
        {
            return DispositionResult.Reject;
        }

        return seen.Contains(DispositionResult.Quarantine) ? DispositionResult.Quarantine : DispositionResult.None;
    }
}

/// <summary>One source IP's aggregated activity within the report window.</summary>
public sealed record SourceAggregate(string SourceIp, int Volume, AuthResult SpfResult, AuthResult DkimResult, DispositionResult Disposition);
