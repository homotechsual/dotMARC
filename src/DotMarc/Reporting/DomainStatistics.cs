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
            .Select(g =>
            {
                var spfResult = CombineAuthResult(g.Select(r => r.SpfResult));
                var dkimResult = CombineAuthResult(g.Select(r => r.DkimResult));
                var disposition = CombineDisposition(g.Select(r => r.Disposition));
                var overrideReasonTypes = g.SelectMany(r => r.OverrideReasons).Select(o => o.Type).Distinct().ToList();
                var authDetails = g.SelectMany(r => r.AuthDetails).Select(d => new AuthDetailSummary(d.Mechanism, d.Domain, d.Result)).Distinct().ToList();

                return new SourceAggregate(
                    g.Key,
                    g.Sum(r => r.MessageCount),
                    spfResult,
                    dkimResult,
                    disposition,
                    overrideReasonTypes,
                    authDetails,
                    overrideReasonTypes.Count == 0
                        ? InferFailureReason(disposition, spfResult, dkimResult, g.First().HeaderFrom, authDetails)
                        : null);
            })
            .ToList();

    /// <summary>When a receiver's report omits an explicit &lt;reason&gt;, works out a plain-English
    /// explanation - and a concrete next step - from the raw per-mechanism auth_results instead of
    /// leaving the source unexplained ("no reason given" tells an operator nothing they can act
    /// on). Only meaningful when the message was actually rejected/quarantined by DMARC (neither
    /// check aligned) - a passing message, or one where a policy override already supplied its own
    /// reason, needs no extrapolation. Domain alignment here is a same-or-subdomain heuristic (not
    /// a strict RFC 7489 org-domain lookup, which would need a public suffix list), so treat the
    /// result as a best-effort explanation rather than an authoritative alignment verdict.</summary>
    private static string? InferFailureReason(DispositionResult disposition, AuthResult spfResult, AuthResult dkimResult, string headerFrom, IReadOnlyList<AuthDetailSummary> authDetails)
    {
        if (disposition == DispositionResult.None || spfResult == AuthResult.Pass || dkimResult == AuthResult.Pass)
        {
            return null;
        }

        var spfOutcome = ClassifyMechanism(DmarcAuthMechanism.Spf, headerFrom, authDetails);
        var dkimOutcome = ClassifyMechanism(DmarcAuthMechanism.Dkim, headerFrom, authDetails);

        return $"SPF: {ExplainMechanism(DmarcAuthMechanism.Spf, spfOutcome, headerFrom, authDetails)}. " +
               $"DKIM: {ExplainMechanism(DmarcAuthMechanism.Dkim, dkimOutcome, headerFrom, authDetails)}. " +
               BuildRecommendation(spfOutcome, dkimOutcome);
    }

    private static string ExplainMechanism(DmarcAuthMechanism mechanism, MechanismOutcome outcome, string headerFrom, IReadOnlyList<AuthDetailSummary> authDetails)
    {
        if (outcome == MechanismOutcome.NotCovered)
        {
            return mechanism == DmarcAuthMechanism.Spf
                ? "sender not covered by SPF (no SPF check reported for this source)"
                : "no DKIM signature present";
        }

        var matching = authDetails.Where(d => d.Mechanism == mechanism).ToList();
        if (outcome == MechanismOutcome.Misaligned)
        {
            var passing = matching.First(d => d.Result == DmarcMechanismResult.Pass);
            return $"passed for {passing.Domain}, which doesn't align with the From: domain ({headerFrom})";
        }

        return $"failed ({string.Join(", ", matching.Select(d => d.Result).Distinct())})";
    }

    /// <summary>Per-mechanism outcome behind both the human-readable explanation and the
    /// SPF/DKIM/Both reason-breakdown bucketing - Misaligned means the raw check actually passed
    /// (for a domain other than the header-from one), the most common shape of a legitimate
    /// third-party sender that hasn't configured alignment, as distinct from Failed (a real
    /// negative result) or NotCovered (the report says nothing about this mechanism at all).
    /// InferFailureReason's Pass short-circuit guarantees this is never called for a mechanism
    /// that actually aligned, so an unaligned Pass here always means Misaligned, never a false
    /// "everything's fine".</summary>
    private enum MechanismOutcome { NotCovered, Misaligned, Failed }

    private static MechanismOutcome ClassifyMechanism(DmarcAuthMechanism mechanism, string headerFrom, IReadOnlyList<AuthDetailSummary> authDetails)
    {
        var matching = authDetails.Where(d => d.Mechanism == mechanism).ToList();
        if (matching.Count == 0)
        {
            return MechanismOutcome.NotCovered;
        }

        return matching.Any(d => d.Result == DmarcMechanismResult.Pass) ? MechanismOutcome.Misaligned : MechanismOutcome.Failed;
    }

    private static bool IsAligned(string mechanismDomain, string headerFrom) =>
        string.Equals(mechanismDomain, headerFrom, StringComparison.OrdinalIgnoreCase)
        || headerFrom.EndsWith("." + mechanismDomain, StringComparison.OrdinalIgnoreCase)
        || mechanismDomain.EndsWith("." + headerFrom, StringComparison.OrdinalIgnoreCase);

    /// <summary>Attributes a failing record to SPF, DKIM, or Both - the same three reason-
    /// breakdown buckets InferAuthFailureCategory feeds. SPF/DKIM apply only when one mechanism
    /// has a definite negative signal (Failed) and the other has none at all (NotCovered) - the
    /// one case where a single mechanism is cleanly "to blame". Misalignment on either mechanism,
    /// or both mechanisms having a verdict, always lands in Both: a misaligned sender needs both
    /// records looked at together, and two simultaneous real failures aren't attributable to one
    /// mechanism over the other.</summary>
    private static AuthFailureCategory ClassifyFailureCategory(MechanismOutcome spf, MechanismOutcome dkim)
    {
        if (spf == MechanismOutcome.Misaligned || dkim == MechanismOutcome.Misaligned)
        {
            return AuthFailureCategory.Both;
        }
        if (spf == MechanismOutcome.Failed && dkim == MechanismOutcome.NotCovered)
        {
            return AuthFailureCategory.Spf;
        }
        if (dkim == MechanismOutcome.Failed && spf == MechanismOutcome.NotCovered)
        {
            return AuthFailureCategory.Dkim;
        }
        return AuthFailureCategory.Both;
    }

    private enum AuthFailureCategory { Spf, Dkim, Both }

    /// <summary>The one-line "what should I actually do about this" that turns the SPF/DKIM
    /// explanation into something actionable rather than just diagnostic.</summary>
    private static string BuildRecommendation(MechanismOutcome spf, MechanismOutcome dkim)
    {
        if (spf == MechanismOutcome.Misaligned || dkim == MechanismOutcome.Misaligned)
        {
            return "Recommendation: this looks like a third-party sender (e.g. a marketing or " +
                   "helpdesk platform) that passed its own check but isn't aligned with your domain - " +
                   "ask them to enable sending on your behalf with proper SPF/DKIM alignment, or add " +
                   "their required DNS records yourself if you control the sending domain.";
        }
        if (spf == MechanismOutcome.NotCovered && dkim == MechanismOutcome.NotCovered)
        {
            return "Recommendation: neither mechanism reported any data for this source - confirm you " +
                   "recognize it at all before assuming it's a legitimate sender you simply forgot to authorize.";
        }

        return ClassifyFailureCategory(spf, dkim) switch
        {
            AuthFailureCategory.Spf => "Recommendation: if you recognize this source, add its IP (or its " +
                "provider's SPF include) to your SPF record; if you don't, treat this as unauthorized.",
            AuthFailureCategory.Dkim => "Recommendation: if you recognize this sender, ask them to enable " +
                "DKIM signing (or configure the selector they provide); if you don't, treat this as unauthorized.",
            _ => "Recommendation: both SPF and DKIM failed outright for this source - this looks like an " +
                 "unauthorized sender rather than a configuration gap."
        };
    }

    /// <summary>Buckets every Reject/Quarantine record's message volume by why it was
    /// disposed-against - the direct signal for "does this look like benign forwarding or a real
    /// spoofing attempt". Disposition == None records are excluded (nothing to explain). A record
    /// with multiple reason entries buckets by priority: Benign wins if any entry is
    /// Forwarded/SampledOut/TrustedForwarder/MailingList (one benign explanation is enough), else
    /// LocalPolicy wins if any entry is LocalPolicy, else Other. With no explicit reason at all,
    /// this falls back to SPF/DKIM/Both (see ClassifyFailureCategory) when the record has
    /// per-mechanism AuthDetails to attribute the failure from, and only to the genuinely
    /// uninformative NoReasonGiven bucket when it doesn't - e.g. a report ingested before
    /// AuthDetails existed, or not yet caught up by PollingService's backfill cycle.</summary>
    public static ReasonBreakdown GetReasonBreakdown(IEnumerable<Report> reportsInWindow)
    {
        int benign = 0, localPolicy = 0, other = 0, noReason = 0, spfFailure = 0, dkimFailure = 0, bothFailure = 0;

        foreach (var record in reportsInWindow.SelectMany(r => r.Records).Where(r => r.Disposition != DispositionResult.None))
        {
            var reasonTypes = record.OverrideReasons.Select(o => o.Type).ToList();

            if (reasonTypes.Count == 0)
            {
                if (record.AuthDetails.Count > 0)
                {
                    var authDetails = record.AuthDetails.Select(d => new AuthDetailSummary(d.Mechanism, d.Domain, d.Result)).ToList();
                    var category = ClassifyFailureCategory(
                        ClassifyMechanism(DmarcAuthMechanism.Spf, record.HeaderFrom, authDetails),
                        ClassifyMechanism(DmarcAuthMechanism.Dkim, record.HeaderFrom, authDetails));

                    switch (category)
                    {
                        case AuthFailureCategory.Spf:
                            spfFailure += record.MessageCount;
                            break;
                        case AuthFailureCategory.Dkim:
                            dkimFailure += record.MessageCount;
                            break;
                        default:
                            bothFailure += record.MessageCount;
                            break;
                    }
                }
                else
                {
                    noReason += record.MessageCount;
                }
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

        return new ReasonBreakdown(benign, localPolicy, other, noReason, spfFailure, dkimFailure, bothFailure);
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

/// <summary>One source IP's aggregated activity within the report window. InferredReason is only
/// populated when the receiver gave no override reason but the source still failed DMARC -
/// see DomainStatistics.InferFailureReason.</summary>
public sealed record SourceAggregate(string SourceIp, int Volume, AuthResult SpfResult, AuthResult DkimResult, DispositionResult Disposition, IReadOnlyList<DmarcPolicyOverrideType> OverrideReasonTypes, IReadOnlyList<AuthDetailSummary> AuthDetails, string? InferredReason);

/// <summary>One distinct (mechanism, domain, result) combination seen for a source in-window -
/// deduplicated so a source failing the same way on every report doesn't repeat itself.</summary>
public sealed record AuthDetailSummary(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result);

/// <summary>Reject/Quarantine message volume in a window, bucketed by why it happened. See
/// DomainStatistics.GetReasonBreakdown for the bucketing rules. The three Inferred* fields default
/// to 0 so every pre-existing 4-arg call site (tests, PollingService's empty-breakdown fallback)
/// still compiles unchanged.</summary>
public sealed record ReasonBreakdown(int BenignOverride, int LocalPolicy, int Other, int NoReasonGiven, int InferredSpfFailure = 0, int InferredDkimFailure = 0, int InferredBothFailure = 0)
{
    public int Total => BenignOverride + LocalPolicy + Other + NoReasonGiven + InferredSpfFailure + InferredDkimFailure + InferredBothFailure;
}
