using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Reporting;

/// <summary>What a domain's sender decisions amount to over a set of reports: how many senders nobody has decided on, and how much
/// mail from senders marked Unauthorised receivers blocked or delivered. Failing means the delivered mail also failed DMARC.</summary>
public sealed record SenderDecisionSummary(int UnreviewedSenders, long UnauthorisedBlocked, long UnauthorisedDelivered, long UnauthorisedDeliveredFailing);

public static class SenderDecisionSummaries
{
    private static readonly IReadOnlyDictionary<int, string> NoGroupNames = new Dictionary<int, string>();

    /// <summary>How many senders have no decision at any level: each recognised service, judged by its service-level decision, and each
    /// unrecognised IP, judged by its own. <paramref name="resolve"/> takes a service name (or null) and an IP (or empty).</summary>
    public static int CountUnreviewed(SenderIdentificationResult identification, Func<string?, string, ResolvedSenderDecision> resolve) =>
        identification.Services.Count(row => resolve(row.Name, string.Empty).Status is null)
        + (identification.Unrecognised?.SourceIps ?? []).Count(ip => resolve(null, ip).Status is null);

    /// <summary>Attributes senders the way the Sources tab and the sender alerts do, and resolves each with <see cref="SenderDecisionResolver"/>,
    /// so every place that shows these numbers agrees.</summary>
    public static SenderDecisionSummary Summarise(
        IReadOnlyList<Report> reports, IReadOnlyDictionary<string, string?> ownerByIp, IReadOnlyList<SenderDecision> decisions,
        int domainId, IReadOnlyCollection<int> groupIds, IReadOnlyList<SpfServiceRange>? spfRanges = null)
    {
        var identification = SenderIdentification.Identify(reports, ownerByIp, [], spfRanges);

        ResolvedSenderDecision Resolve(string? serviceName, string ip) =>
            SenderDecisionResolver.Resolve(decisions, domainId, groupIds, serviceName, ip, NoGroupNames);

        var unreviewed = CountUnreviewed(identification, Resolve);

        long blocked = 0, delivered = 0, deliveredFailing = 0;
        foreach (var source in reports.SelectMany(report => report.Records).GroupBy(record => record.SourceIp, StringComparer.Ordinal))
        {
            if (Resolve(identification.ServiceByIp.GetValueOrDefault(source.Key), source.Key).Status != SenderDecisionStatus.Unauthorised)
            {
                continue;
            }

            foreach (var record in source)
            {
                if (record.Disposition == DispositionResult.None)
                {
                    delivered += record.MessageCount;
                    deliveredFailing += DomainStatistics.IsPassing(record) ? 0 : record.MessageCount;
                }
                else
                {
                    blocked += record.MessageCount;
                }
            }
        }

        return new SenderDecisionSummary(unreviewed, blocked, delivered, deliveredFailing);
    }
}
