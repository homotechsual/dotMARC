using DotMarc.Data;

namespace DotMarc.Reporting;

public enum SenderDecisionLevel { None, Domain, Group, Baseline }

public static class SenderDecisionStatusLabels
{
    /// <summary>How a status reads on the page and in conflict text.</summary>
    public static string Label(this SenderDecisionStatus status) => status switch
    {
        SenderDecisionStatus.Authorised => "Authorised",
        SenderDecisionStatus.UnderReview => "Under review",
        _ => "Unauthorised",
    };
}

/// <summary>The decision that applies to one sender on one domain. <see cref="Status"/> is null for Unreviewed.
/// <see cref="Conflict"/> says so when several of the domain's groups disagree and none of the domain's own decisions settles it.</summary>
public sealed record ResolvedSenderDecision(SenderDecisionStatus? Status, SenderDecisionLevel Level, SenderDecision? Decision, string? Conflict);

public static class SenderDecisionResolver
{
    /// <summary>Picks the decision that applies, most specific first: the domain's IP decision, the domain's service decision, the
    /// domain's groups' service decisions, then the baseline. Groups that disagree resolve to the most cautious status
    /// (Unauthorised, then Under review, then Authorised). A source with no service name can only match an IP decision.</summary>
    public static ResolvedSenderDecision Resolve(
        IReadOnlyList<SenderDecision> decisions,
        int domainId,
        IReadOnlyCollection<int> groupIds,
        string? serviceName,
        string ip,
        IReadOnlyDictionary<int, string> groupNames)
    {
        var onDomain = decisions.Where(decision => decision.Scope == SenderDecisionScope.Domain && decision.DomainId == domainId).ToList();

        var ipDecision = onDomain.FirstOrDefault(decision => decision.Kind == SenderDecisionKind.Ip && decision.Key == ip);
        if (ipDecision is not null)
        {
            return Found(ipDecision, SenderDecisionLevel.Domain);
        }

        if (serviceName is null)
        {
            return new ResolvedSenderDecision(null, SenderDecisionLevel.None, null, null);
        }

        var serviceDecision = onDomain.FirstOrDefault(decision => decision.Kind == SenderDecisionKind.Service && decision.Key == serviceName);
        if (serviceDecision is not null)
        {
            return Found(serviceDecision, SenderDecisionLevel.Domain);
        }

        var onGroups = decisions
            .Where(decision => decision.Scope == SenderDecisionScope.Group && decision.GroupId is { } groupId && groupIds.Contains(groupId)
                && decision.Kind == SenderDecisionKind.Service && decision.Key == serviceName)
            .OrderBy(decision => groupNames.GetValueOrDefault(decision.GroupId!.Value, string.Empty), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (onGroups.Count > 0)
        {
            var winner = onGroups.OrderByDescending(decision => Caution(decision.Status)).First();
            var disagree = onGroups.Select(decision => decision.Status).Distinct().Count() > 1;
            var conflict = disagree
                ? string.Join(", ", onGroups.Select(decision => $"{groupNames.GetValueOrDefault(decision.GroupId!.Value, "A group")} says {decision.Status.Label()}"))
                : null;
            return new ResolvedSenderDecision(winner.Status, SenderDecisionLevel.Group, winner, conflict);
        }

        var baseline = decisions.FirstOrDefault(decision =>
            decision.Scope == SenderDecisionScope.Baseline && decision.Kind == SenderDecisionKind.Service && decision.Key == serviceName);
        return baseline is not null
            ? Found(baseline, SenderDecisionLevel.Baseline)
            : new ResolvedSenderDecision(null, SenderDecisionLevel.None, null, null);
    }

    private static ResolvedSenderDecision Found(SenderDecision decision, SenderDecisionLevel level) =>
        new(decision.Status, level, decision, null);

    private static int Caution(SenderDecisionStatus status) => status switch
    {
        SenderDecisionStatus.Unauthorised => 2,
        SenderDecisionStatus.UnderReview => 1,
        _ => 0,
    };
}
