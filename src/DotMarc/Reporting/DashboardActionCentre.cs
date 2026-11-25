using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Notifications;

namespace DotMarc.Reporting;

public sealed record DashboardProblem(string Description, string Action, string Tab, bool NeedsChecking = false);
public sealed record DashboardAttention(Domain Domain, IReadOnlyList<DashboardProblem> Problems, int SenderReviews, double? PassRate, bool MissingReport)
{
    public bool MatchesCategory(string category) => category switch
    {
        "dns" => Problems.Any(problem => !problem.NeedsChecking && problem.Action != "Investigate reports"),
        "missing" => MissingReport,
        "senders" => SenderReviews > 0,
        "checking" => Problems.Any(problem => problem.NeedsChecking),
        "action" => HasConfirmedProblem || SenderReviews > 0,
        _ => throw new ArgumentException("Unknown attention category.", nameof(category))
    };
    public bool MatchesCategories(string? categories) => categories is null || categories.Split(',').Any(MatchesCategory);
    public bool HasConfirmedProblem => Problems.Any(problem => !problem.NeedsChecking);
    public int Priority => HasConfirmedProblem ? 0 : SenderReviews > 0 ? 1 : 2;
}

public sealed record DashboardInvestigation(Domain Domain, IReadOnlyList<Report> Reports, long Quarantined, long Rejected, double? PassRate)
{
    public long Total => Quarantined + Rejected;
}

public sealed record DashboardDay(DateOnly Date, long Quarantined, long Rejected);

/// <summary>Scoped, filtered facts, not a baseline health score. Seven UTC calendar days include today,
/// bucketed by report receipt, because individual messages have no timestamp.</summary>
public sealed record DashboardActionCentre(
    int DomainCount, int MonitoredCount, long Messages, int Reports, int ReportingSources, double? PassRate,
    int MissingReports, int SenderReviews, long SevenDayMessages, DateTimeOffset SnapshotUtc,
    IReadOnlyList<DashboardAttention> Attention, IReadOnlyList<DashboardInvestigation> Investigations,
    IReadOnlyList<DashboardDay> Days)
{
    public long Quarantined => Days.Sum(day => day.Quarantined);
    public long Rejected => Days.Sum(day => day.Rejected);
    public int ProblemDomains => Attention.Count(row => row.HasConfirmedProblem || row.SenderReviews > 0);
    public int UncheckedDomains => Attention.Count(row => row.Problems.Any(problem => problem.NeedsChecking));

    public static DashboardActionCentre Build(
        IReadOnlyList<Domain> domains, IReadOnlyDictionary<int, int> senderReviews, DnsRecordSettings settings, DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        var cutoff = DomainStatistics.GetWindowCutoffUtc(now);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var firstDay = today.AddDays(-6);
        var sevenDayCutoff = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var attention = new List<DashboardAttention>();
        var investigations = new List<DashboardInvestigation>();
        var reports = domains.SelectMany(domain => domain.Reports).Where(report => report.ReceivedUtc >= cutoff && report.ReceivedUtc <= now).ToList();
        var total = reports.SelectMany(report => report.Records).Sum(record => (long)record.MessageCount);
        var passing = reports.SelectMany(report => report.Records).Where(DomainStatistics.IsPassing).Sum(record => (long)record.MessageCount);
        var missing = 0;
        foreach (var domain in domains)
        {
            var problems = Problems(domain, settings);
            var reportMissing = ExpectsReports(domain) && (domain.LastReportReceivedUtc is null || domain.LastReportReceivedUtc < now.AddDays(-2));
            if (reportMissing)
            {
                missing++;
                problems.Add(new("No expected report in the last two days", "Investigate reports", "overview"));
            }
            var reviews = senderReviews.GetValueOrDefault(domain.Id);
            if (problems.Count > 0 || reviews > 0)
                attention.Add(new(domain, problems, reviews, CalculatePassRate(domain.Reports.Where(report => report.ReceivedUtc >= cutoff && report.ReceivedUtc <= now)), reportMissing));

            var recent = domain.Reports.Where(report => report.ReceivedUtc >= sevenDayCutoff && report.ReceivedUtc <= now).ToList();
            var quarantined = Volume(recent, DispositionResult.Quarantine);
            var rejected = Volume(recent, DispositionResult.Reject);
            if (quarantined + rejected > 0)
                investigations.Add(new(domain, recent, quarantined, rejected, CalculatePassRate(recent)));
        }
        var daily = investigations.SelectMany(row => row.Reports).SelectMany(report => report.Records.Select(record =>
            (Day: DateOnly.FromDateTime(report.ReceivedUtc.UtcDateTime), record.Disposition, record.MessageCount))).ToList();
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var day = firstDay.AddDays(offset);
            return new DashboardDay(day,
                daily.Where(entry => entry.Day == day && entry.Disposition == DispositionResult.Quarantine).Sum(entry => (long)entry.MessageCount),
                daily.Where(entry => entry.Day == day && entry.Disposition == DispositionResult.Reject).Sum(entry => (long)entry.MessageCount));
        }).ToList();
        return new(domains.Count, domains.Count(domain => domain.IsMonitored), total, reports.Count,
            reports.Select(report => report.ReportingOrg).Distinct().Count(), total == 0 ? null : (double)passing / total,
            missing, domains.Sum(domain => senderReviews.GetValueOrDefault(domain.Id)),
            reports.Where(report => report.ReceivedUtc >= sevenDayCutoff).SelectMany(report => report.Records).Sum(record => (long)record.MessageCount), now,
            attention.OrderBy(row => row.Priority).ThenByDescending(row => row.Problems.Count(problem => !problem.NeedsChecking))
                .ThenByDescending(row => row.SenderReviews).ThenBy(row => row.Domain.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            investigations.OrderByDescending(row => row.Total).ThenBy(row => row.Domain.Name, StringComparer.OrdinalIgnoreCase).ToList(), days);
    }

    public static string DomainUrl(Domain domain, string tab = "overview") => $"/domains/{Uri.EscapeDataString(domain.Name)}/{tab}";
    private static bool ExpectsReports(Domain domain) => domain.IsMonitored && domain.SpfCheckStatus != SpfCheckStatus.NullSpf;
    private static long Volume(IEnumerable<Report> reports, DispositionResult disposition) =>
        reports.SelectMany(report => report.Records).Where(record => record.Disposition == disposition).Sum(record => (long)record.MessageCount);
    private static double? CalculatePassRate(IEnumerable<Report> reports)
    {
        var records = reports.SelectMany(report => report.Records).ToList();
        var total = records.Sum(record => (long)record.MessageCount);
        return total == 0 ? null : (double)records.Where(DomainStatistics.IsPassing).Sum(record => (long)record.MessageCount) / total;
    }

    private static List<DashboardProblem> Problems(Domain domain, DnsRecordSettings settings)
    {
        var problems = new List<DashboardProblem>();
        void Add(string description, bool checking = false, string tab = "overview") =>
            problems.Add(new(description, checking ? "Check configuration" : "Review configuration", tab, checking));
        if (domain.DmarcCheckStatus == DmarcCheckStatus.NotChecked)
            Add("DMARC has not been checked", true);
        else if (domain.DmarcCheckStatus == DmarcCheckStatus.MissingOwnRecord)
            Add("No DMARC record");
        else if (domain.DmarcCheckStatus == DmarcCheckStatus.Misconfigured)
        {
            // The stored status also flags an intentionally different/missing rua. Without a reporting
            // requirement, use the actual record syntax instead of treating dotMARC's absence as a fault.
            if (ExpectsReports(domain))
                Add("DMARC record or reporting destination is misconfigured");
            else if (domain.DmarcRecords.Count == 0)
                Add("DMARC record needs verification", true);
            else
            {
                var analysis = DmarcRecordAnalysis.Analyse(domain.DmarcRecords);
                if (analysis.Records.Count == 0 || analysis.Problems.Any(problem => problem.Kind.IsBreaking()))
                    Add("DMARC record is malformed");
            }
        }
        else if (ExpectsReports(domain) && domain.DmarcCheckStatus == DmarcCheckStatus.TooManyReportAddresses)
            Add("Too many DMARC reporting addresses");
        if (ExpectsReports(domain))
        {
            if (domain.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.Missing || domain.DmarcCheckStatus == DmarcCheckStatus.MissingAuthorizationRecord)
                Add("DMARC reporting authorization is missing");
            else if (domain.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.NotChecked)
                Add("DMARC reporting authorization has not been checked", true);
        }
        if (domain.SpfCheckStatus == SpfCheckStatus.NotChecked)
            Add("SPF has not been checked", true);
        else if (domain.SpfCheckStatus is not (SpfCheckStatus.Ok or SpfCheckStatus.NullSpf))
            Add($"SPF: {SpfStatusPresentation.GetLabel(domain.SpfCheckStatus)}");
        if (domain.SpfCheckStatus != SpfCheckStatus.NullSpf)
        {
            if (domain.DkimSelectors.Count > 0)
            {
                if (domain.DkimCheckStatus == DkimCheckStatus.NotConfigured)
                    Add("Configured DKIM selectors have not been checked", true);
                else if (domain.DkimCheckStatus != DkimCheckStatus.Ok)
                    Add($"DKIM: {DkimStatusPresentation.GetLabel(domain.DkimCheckStatus)}");
            }
            if (domain.MtaStsEnabled)
            {
                if (domain.MtaStsStatus == MtaStsStatus.Failed)
                    Add("MTA-STS hosting failed", tab: "mta-sts");
                else if (domain.MtaStsStatus != MtaStsStatus.Active)
                    Add($"MTA-STS: {MtaStsStatusPresentation.GetLabel(domain.MtaStsStatus)}", true, "mta-sts");
                if (domain.TlsrptCheckStatus == TlsrptCheckStatus.NotChecked)
                    Add("TLS reporting has not been checked", true, "mta-sts");
                else if (domain.TlsrptCheckStatus != TlsrptCheckStatus.Ok)
                    Add("TLS reporting record is missing or misconfigured", tab: "mta-sts");
            }
        }
        if (domain.IsMonitored)
        {
            var target = DmarcTargetResolver.Resolve(domain, settings);
            var outcome = DmarcTargetCheck.Outcome(domain, target);
            if (outcome == DmarcTargetOutcome.Below)
                Add($"Below DMARC target: {string.Join("; ", DmarcTargetCheck.Reasons(domain, target))}");
            else if (outcome == DmarcTargetOutcome.NeedsChecking && domain.DmarcCheckStatus != DmarcCheckStatus.NotChecked)
                Add("DMARC target needs a recorded DNS check", true);
        }
        return problems;
    }
}
