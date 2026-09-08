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
    /// domain's own pass rate equally - a domain sending 10 messages in the window should not move
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
                CombineDisposition(g.Select(r => r.Disposition)),
                g.SelectMany(r => r.OverrideReasons).Select(o => o.Type).Distinct().ToList(),
                g.SelectMany(r => r.AuthDetails).Select(d => new AuthDetailSummary(d.Mechanism, d.Domain, d.Result)).Distinct().ToList()))
            .ToList();

    /// <summary>Buckets every Reject/Quarantine record's message volume by why it was
    /// disposed-against - the direct signal for "does this look like benign forwarding or a real
    /// spoofing attempt". Disposition == None records are excluded (nothing to explain). A record
    /// with multiple reason entries buckets by priority: Benign wins if any entry is
    /// Forwarded/SampledOut/TrustedForwarder/MailingList (one benign explanation is enough), else
    /// LocalPolicy wins if any entry is LocalPolicy, else Other. No reason at all is the least
    /// benign-looking signal, since a real receiver usually only omits &lt;reason&gt; when
    /// disposition matches assessment exactly.</summary>
    public static ReasonBreakdown GetReasonBreakdown(IEnumerable<Report> reportsInWindow)
    {
        int benign = 0, localPolicy = 0, other = 0, noReason = 0;

        foreach (var record in reportsInWindow.SelectMany(r => r.Records).Where(r => r.Disposition != DispositionResult.None))
        {
            var reasonTypes = record.OverrideReasons.Select(o => o.Type).ToList();

            if (reasonTypes.Count == 0)
            {
                noReason += record.MessageCount;
            }
            else if (reasonTypes.Any(IsBenignOverride))
            {
                benign += record.MessageCount;
            }
            else if (reasonTypes.Contains(DmarcPolicyOverrideType.LocalPolicy))
            {
                localPolicy += record.MessageCount;
            }
            else
            {
                other += record.MessageCount;
            }
        }

        return new ReasonBreakdown(benign, localPolicy, other, noReason);
    }

    /// <summary>Same bucketing as the single-domain overload, summed across every supplied
    /// domain's in-window reports - mirrors GetOverallPassRate's existing multi-domain shape.</summary>
    public static ReasonBreakdown GetReasonBreakdown(IEnumerable<IEnumerable<Report>> perDomainReportsInWindow) =>
        GetReasonBreakdown(perDomainReportsInWindow.SelectMany(reports => reports));

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

    private static bool IsBenignOverride(DmarcPolicyOverrideType type) =>
        type is DmarcPolicyOverrideType.Forwarded or DmarcPolicyOverrideType.SampledOut or DmarcPolicyOverrideType.TrustedForwarder or DmarcPolicyOverrideType.MailingList;
}

/// <summary>One source IP's aggregated activity within the report window.</summary>
public sealed record SourceAggregate(string SourceIp, int Volume, AuthResult SpfResult, AuthResult DkimResult, DispositionResult Disposition, IReadOnlyList<DmarcPolicyOverrideType> OverrideReasonTypes, IReadOnlyList<AuthDetailSummary> AuthDetails);

/// <summary>One distinct (mechanism, domain, result) combination seen for a source in-window -
/// deduplicated so a source failing the same way on every report doesn't repeat itself.</summary>
public sealed record AuthDetailSummary(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result);

/// <summary>Reject/Quarantine message volume in a window, bucketed by why it happened. See
/// DomainStatistics.GetReasonBreakdown for the bucketing rules.</summary>
public sealed record ReasonBreakdown(int BenignOverride, int LocalPolicy, int Other, int NoReasonGiven)
{
    public int Total => BenignOverride + LocalPolicy + Other + NoReasonGiven;
}
