using System.Globalization;
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Reporting;

public sealed record SenderAlertSettings(int NewSenderMinVolume, int UnauthorisedDeliveredMinVolume);

/// <summary>Everything the sender alerts need about one domain. <see cref="Reports"/> are the 30-day window, and
/// <see cref="NewIps"/> are the source IPs already confirmed to have no history before the "new" period.</summary>
public sealed record SenderAlertInputs(
    int DomainId,
    IReadOnlyCollection<int> GroupIds,
    IReadOnlyList<Report> Reports,
    IReadOnlyList<SenderDecision> Decisions,
    IReadOnlyDictionary<string, string?> OwnerByIp,
    IReadOnlySet<string> NewIps,
    SenderAlertSettings Settings,
    IReadOnlyList<SpfServiceRange>? SpfRanges = null);

/// <summary>One sender an alert is about: a service name, or the IP of a source no service was recognised for.</summary>
public sealed record SenderAlertFinding(string Sender, long Messages);

public sealed record SenderAlertResult(IReadOnlyList<SenderAlertFinding> NewUnreviewed, IReadOnlyList<SenderAlertFinding> UnauthorisedDelivered);

public static class SenderAlertEvaluator
{
    private static readonly IReadOnlyDictionary<int, string> NoGroupNames = new Dictionary<int, string>();

    /// <summary>The IPs that might be new: those whose every record in <paramref name="reports"/> arrived on or after
    /// <paramref name="newSinceUtc"/>. The caller confirms they have no history before the window, since an IP that reports
    /// rarely can look new here and not be.</summary>
    public static IReadOnlySet<string> NewSourceCandidates(IReadOnlyList<Report> reports, DateTimeOffset newSinceUtc) =>
        reports
            .SelectMany(report => report.Records.Select(record => (record.SourceIp, report.ReceivedUtc)))
            .GroupBy(entry => entry.SourceIp, StringComparer.Ordinal)
            .Where(group => group.Min(entry => entry.ReceivedUtc) >= newSinceUtc)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Works out the two lists the sender alerts are raised from. A sender is attributed the same way the Sources tab does
    /// it, so an alert never names an IP the table calls Microsoft 365.</summary>
    public static SenderAlertResult Evaluate(SenderAlertInputs inputs)
    {
        var records = inputs.Reports.SelectMany(report => report.Records).ToList();
        var identification = SenderIdentification.Identify(inputs.Reports, inputs.OwnerByIp, [], inputs.SpfRanges);

        ResolvedSenderDecision Resolve(string? serviceName, string ip) =>
            SenderDecisionResolver.Resolve(inputs.Decisions, inputs.DomainId, inputs.GroupIds, serviceName, ip, NoGroupNames);

        // ---- New and unreviewed: a service by its service-level decision, an unrecognised IP by its own ----
        var newUnreviewed = new List<SenderAlertFinding>();
        foreach (var row in identification.Services)
        {
            if (row.SourceIps.All(inputs.NewIps.Contains)
                && row.Messages >= inputs.Settings.NewSenderMinVolume
                && Resolve(row.Name, string.Empty).Status is null)
            {
                newUnreviewed.Add(new SenderAlertFinding(row.Name, row.Messages));
            }
        }

        var messagesByIp = records.GroupBy(record => record.SourceIp, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Sum(record => (long)record.MessageCount), StringComparer.Ordinal);
        foreach (var ip in identification.Unrecognised?.SourceIps ?? [])
        {
            if (inputs.NewIps.Contains(ip) && messagesByIp[ip] >= inputs.Settings.NewSenderMinVolume && Resolve(null, ip).Status is null)
            {
                newUnreviewed.Add(new SenderAlertFinding(ip, messagesByIp[ip]));
            }
        }

        // ---- Unauthorised and delivered: judged per IP, so an IP decision overrides its service's ----
        var deliveredBySender = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var group in records.GroupBy(record => record.SourceIp, StringComparer.Ordinal))
        {
            var service = identification.ServiceByIp.GetValueOrDefault(group.Key);
            if (Resolve(service, group.Key).Status != SenderDecisionStatus.Unauthorised)
            {
                continue;
            }

            var delivered = group.Where(record => record.Disposition == DispositionResult.None).Sum(record => (long)record.MessageCount);
            if (delivered > 0)
            {
                var sender = service ?? group.Key;
                deliveredBySender[sender] = deliveredBySender.GetValueOrDefault(sender) + delivered;
            }
        }

        var unauthorisedDelivered = deliveredBySender
            .Where(entry => entry.Value >= inputs.Settings.UnauthorisedDeliveredMinVolume)
            .Select(entry => new SenderAlertFinding(entry.Key, entry.Value))
            .ToList();

        return new SenderAlertResult(Busiest(newUnreviewed), Busiest(unauthorisedDelivered));
    }

    private static IReadOnlyList<SenderAlertFinding> Busiest(List<SenderAlertFinding> findings) =>
        findings.OrderByDescending(finding => finding.Messages).ThenBy(finding => finding.Sender, StringComparer.Ordinal).ToList();
}

/// <summary>The words of the two sender alerts, in one place so the alerting cycle and the demo data say exactly the same thing.</summary>
public static class SenderAlertMessages
{
    public const string NewSendersTitle = "New senders to review";
    public const string UnauthorisedDeliveredTitle = "Mail from an unauthorised sender is being delivered";

    public static string NewSenders(string domainName, IReadOnlyList<SenderAlertFinding> findings)
    {
        var plural = findings.Count != 1;
        return $"{domainName} has {findings.Count} new sender{(plural ? "s" : "")} that nobody has reviewed: {List(findings)}. " +
               $"Open the domain's Sources tab and mark {(plural ? "each one" : "it")} Authorised, Unauthorised or Under review.";
    }

    public static string UnauthorisedDelivered(string domainName, IReadOnlyList<SenderAlertFinding> findings, DmarcPolicyLevel policy) =>
        $"Mail from {(findings.Count == 1 ? "a sender" : "senders")} marked unauthorised is being delivered for {domainName}: {List(findings)}. " +
        SenderDecisionPresentation.PolicyLine(policy);

    private static string List(IReadOnlyList<SenderAlertFinding> findings) =>
        string.Join(", ", findings.Select(finding => $"{finding.Sender} ({finding.Messages.ToString("N0", CultureInfo.InvariantCulture)} message{(finding.Messages == 1 ? "" : "s")})"));
}
