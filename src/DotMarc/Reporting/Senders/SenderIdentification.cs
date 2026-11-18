using DotMarc.Data;

namespace DotMarc.Reporting;

public enum SenderFailureCause { SpfNotAligned, DkimNotAligned, DkimMissing, Both }

/// <summary>One line of the sender table: a service, or the sources we couldn't match to one (<see cref="IsUnrecognised"/>).
/// <see cref="Evidence"/> is the highest score of any source attributed to the service, null for the unrecognised row.</summary>
public sealed record SenderServiceRow(
    string Name,
    bool IsUnrecognised,
    long Messages,
    double Share,
    double? Evidence,
    double? PassRate,
    long Passing,
    long Failing,
    long ActedOn,
    SenderFailureCause? DominantCause,
    string? Fix,
    IReadOnlyList<string> EvidenceLines,
    IReadOnlyList<string> SourceIps);

public sealed record SenderIdentificationResult(
    IReadOnlyList<SenderServiceRow> Services,
    SenderServiceRow? Unrecognised,
    IReadOnlyDictionary<string, string> ServiceByIp);

public static class SenderIdentification
{
    public const string UnrecognisedName = "Unrecognised";

    /// <summary>Groups a domain's reports by sending service. Each source IP is scored once from every auth detail seen for it, and
    /// attributed to its best-scoring service when that score reaches <see cref="SenderScoring.AttributionThreshold"/>. Failing
    /// messages are the records where neither SPF nor DKIM passed aligned.</summary>
    public static SenderIdentificationResult Identify(
        IEnumerable<Report> reportsInWindow,
        IReadOnlyDictionary<string, string?> ownerByIp,
        IReadOnlyCollection<string> spfIncludedNames) =>
        IdentifyRecords(reportsInWindow.SelectMany(report => report.Records), ownerByIp, spfIncludedNames);

    /// <summary>The same, for callers that load records directly (with their <c>AuthDetails</c>) rather than whole reports.</summary>
    public static SenderIdentificationResult IdentifyRecords(
        IEnumerable<ReportRecord> recordsInWindow,
        IReadOnlyDictionary<string, string?> ownerByIp,
        IReadOnlyCollection<string> spfIncludedNames)
    {
        var records = recordsInWindow.ToList();
        var totalMessages = records.Sum(record => (long)record.MessageCount);

        var serviceByIp = new Dictionary<string, string>(StringComparer.Ordinal);
        var scoreByIp = new Dictionary<string, ServiceScore>(StringComparer.Ordinal);
        foreach (var ipGroup in records.GroupBy(record => record.SourceIp, StringComparer.Ordinal))
        {
            var details = ipGroup
                .SelectMany(record => record.AuthDetails)
                .Select(detail => new AuthDetailSummary(detail.Mechanism, detail.Domain, detail.Result, detail.Selector))
                .Distinct()
                .ToList();
            var best = SenderScoring.Score(new SenderSourceInput(ipGroup.Key, ownerByIp.GetValueOrDefault(ipGroup.Key), details), spfIncludedNames)
                .FirstOrDefault();
            if (best is not null && best.Score >= SenderScoring.AttributionThreshold)
            {
                serviceByIp[ipGroup.Key] = best.Service.Name;
                scoreByIp[ipGroup.Key] = best;
            }
        }

        var catalogueOrder = SenderServiceCatalog.All.Select((service, index) => (service.Name, index)).ToDictionary(pair => pair.Name, pair => pair.index);

        var services = records
            .Where(record => serviceByIp.ContainsKey(record.SourceIp))
            .GroupBy(record => serviceByIp[record.SourceIp])
            .Select(group => BuildRow(group.Key, isUnrecognised: false, group.ToList(), totalMessages, scoreByIp, ownerByIp))
            .OrderByDescending(row => row.Messages)
            .ThenBy(row => catalogueOrder[row.Name])
            .ToList();

        var unmatched = records.Where(record => !serviceByIp.ContainsKey(record.SourceIp)).ToList();
        var unrecognised = unmatched.Count == 0
            ? null
            : BuildRow(UnrecognisedName, isUnrecognised: true, unmatched, totalMessages, scoreByIp, ownerByIp);

        return new SenderIdentificationResult(services, unrecognised, serviceByIp);
    }

    /// <summary>The cause first, then the fix, naming the service and the mechanism. Never exact records to add: those vary by
    /// account and go stale.</summary>
    public static string FixText(string serviceName, SenderFailureCause cause) => cause switch
    {
        SenderFailureCause.SpfNotAligned =>
            $"{serviceName} sends mail whose SPF passes for its own domain, not yours. Set up a custom return path on your domain with {serviceName}, then recheck.",
        SenderFailureCause.DkimNotAligned =>
            $"{serviceName} signs mail with its own domain, not yours. Set up DKIM signing as your domain with {serviceName}, then recheck.",
        SenderFailureCause.DkimMissing =>
            $"{serviceName} sends mail with no DKIM signature. Turn on DKIM signing for your domain with {serviceName}, then recheck.",
        _ =>
            $"{serviceName} fails both SPF and DKIM for your domain. If you use it, authorise it for your domain with SPF and DKIM. If you don't, treat it as unauthorised.",
    };

    private static SenderServiceRow BuildRow(
        string name,
        bool isUnrecognised,
        IReadOnlyList<ReportRecord> records,
        long totalMessages,
        IReadOnlyDictionary<string, ServiceScore> scoreByIp,
        IReadOnlyDictionary<string, string?> ownerByIp)
    {
        var messages = records.Sum(record => (long)record.MessageCount);
        var failingRecords = records.Where(record => !DomainStatistics.IsPassing(record)).ToList();
        var failing = failingRecords.Sum(record => (long)record.MessageCount);
        var actedOn = failingRecords.Where(record => record.Disposition != DispositionResult.None).Sum(record => (long)record.MessageCount);

        SenderFailureCause? dominantCause = null;
        if (failing > 0)
        {
            dominantCause = failingRecords
                .GroupBy(record => DomainStatistics.ClassifyFailureCause(
                    record.AuthDetails.Select(detail => new AuthDetailSummary(detail.Mechanism, detail.Domain, detail.Result, detail.Selector)).ToList(),
                    record.HeaderFrom))
                .OrderByDescending(group => group.Sum(record => (long)record.MessageCount))
                .ThenBy(group => group.Key)
                .First().Key;
        }

        var ips = records.Select(record => record.SourceIp).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var attributedScores = ips.Where(scoreByIp.ContainsKey).Select(ip => scoreByIp[ip]).ToList();
        var evidenceLines = isUnrecognised
            ? ips.Select(ip => ownerByIp.GetValueOrDefault(ip)).Where(owner => !string.IsNullOrWhiteSpace(owner))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(owner => $"IP owned by {owner}").ToList()
            : attributedScores.SelectMany(score => score.Evidence.Select(evidence => evidence.Description)).Distinct().ToList();

        return new SenderServiceRow(
            name,
            isUnrecognised,
            messages,
            totalMessages == 0 ? 0 : (double)messages / totalMessages,
            isUnrecognised ? null : attributedScores.Max(score => score.Score),
            messages == 0 ? null : (double)(messages - failing) / messages,
            messages - failing,
            failing,
            actedOn,
            dominantCause,
            dominantCause is { } cause
                ? isUnrecognised
                    ? "These sources aren't recognised and are failing DMARC. If you don't recognise them, treat them as unauthorised."
                    : FixText(name, cause)
                : null,
            evidenceLines,
            ips);
    }
}
