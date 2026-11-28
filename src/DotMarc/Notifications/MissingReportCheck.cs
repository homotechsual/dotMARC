using DotMarc.Data;

namespace DotMarc.Notifications;

public static class MissingReportCheck
{
    public static bool IsOverdue(Domain domain, int days, DateTimeOffset now)
    {
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days));
        if (!domain.IsMonitored || domain.SpfCheckStatus == SpfCheckStatus.NullSpf) return false;
        return (domain.LastReportReceivedUtc ?? domain.FirstSeenUtc) < now.AddDays(-days);
    }

    public static bool IsOverdue(Domain domain, AlertRuleSet rules, DateTimeOffset now) =>
        rules.For(AlertTypes.MissedReport, new DomainFacts(domain.Id,
                domain.Groups.Select(group => group.Id).ToHashSet(), domain.Tags.Select(tag => tag.Id).ToHashSet()))
            .Any(rule => IsOverdue(domain, rule.Int(AlertRuleParameterKeys.MissingDays), now));
}
