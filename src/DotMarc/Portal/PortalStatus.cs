using System.Globalization;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Reporting;

namespace DotMarc.Portal;

/// <summary>Attention first, so sorting by it lists the domains that need something at the top.</summary>
public enum PortalHealth { NeedsAttention, NoReportsYet, MonitoringOnly, Protected }

public sealed record PortalDomainStatus(PortalHealth Health, IReadOnlyList<string> Reasons);

/// <summary>Turns a domain's health checks, reports and open alerts into the plain-language status a client sees.</summary>
public static class PortalStatus
{
    public const string MonitoringOnlyReason = "The DMARC policy only monitors, so mail spoofing this domain isn't blocked yet.";
    public const string NoReportsYetReason = "No DMARC reports have arrived yet. They usually start within a few days.";

    public static PortalDomainStatus For(Domain domain, bool hasReports, IReadOnlyList<AlertEvent> openAlerts)
    {
        var problems = FailingChecks(domain).Concat(openAlerts.Select(alert => alert.Title)).Distinct().ToList();
        if (problems.Count > 0)
        {
            return new PortalDomainStatus(PortalHealth.NeedsAttention, problems);
        }

        if (!hasReports)
        {
            return new PortalDomainStatus(PortalHealth.NoReportsYet, [NoReportsYetReason]);
        }

        return domain.DmarcPolicy is DmarcPolicyLevel.Quarantine or DmarcPolicyLevel.Reject
            ? new PortalDomainStatus(PortalHealth.Protected, [])
            : new PortalDomainStatus(PortalHealth.MonitoringOnly, [MonitoringOnlyReason]);
    }

    /// <summary>One line per failing check, "Check: what's wrong", using the labels the app already shows. Checks not run
    /// yet, not configured, or not applicable aren't failures.</summary>
    public static IEnumerable<string> FailingChecks(Domain domain)
    {
        if (domain.DmarcCheckStatus is DmarcCheckStatus.MissingOwnRecord or DmarcCheckStatus.Misconfigured or DmarcCheckStatus.MissingAuthorizationRecord)
            yield return $"DMARC: {DmarcStatusPresentation.GetLabel(domain.DmarcCheckStatus)}";
        if (domain.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.Missing)
            yield return $"DMARC reporting: {DmarcAuthorizationStatusPresentation.GetLabel(domain.DmarcAuthorizationCheckStatus)}";
        if (domain.SpfCheckStatus is not (SpfCheckStatus.NotChecked or SpfCheckStatus.Ok or SpfCheckStatus.NullSpf))
            yield return $"SPF: {SpfStatusPresentation.GetLabel(domain.SpfCheckStatus)}";
        if (domain.MxCheckStatus is MxCheckStatus.MissingRecord or MxCheckStatus.UnresolvableTarget)
            yield return $"MX: {MxStatusPresentation.GetLabel(domain.MxCheckStatus)}";
        if (domain.DkimCheckStatus is DkimCheckStatus.Missing or DkimCheckStatus.Misconfigured)
            yield return $"DKIM: {DkimStatusPresentation.GetLabel(domain.DkimCheckStatus)}";
        if (domain.TlsrptCheckStatus is TlsrptCheckStatus.MissingOwnRecord or TlsrptCheckStatus.Misconfigured)
            yield return $"TLS reporting: {TlsrptStatusPresentation.GetLabel(domain.TlsrptCheckStatus)}";
        if (domain.MtaStsStatus == MtaStsStatus.Failed)
            yield return $"MTA-STS: {MtaStsStatusPresentation.GetLabel(domain.MtaStsStatus)}";
    }

    /// <summary>One sentence summing up all of a client's domains, for the top of the portal.</summary>
    public static string Verdict(IReadOnlyList<PortalDomainStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return "There are no domains to show yet.";
        }

        var protectedCount = statuses.Count(status => status.Health == PortalHealth.Protected);
        var attentionCount = statuses.Count(status => status.Health == PortalHealth.NeedsAttention);
        if (protectedCount == statuses.Count)
        {
            return statuses.Count == 1
                ? "Your domain is protected."
                : string.Create(CultureInfo.InvariantCulture, $"All {statuses.Count} domains are protected.");
        }

        if (statuses.Count == 1)
        {
            return attentionCount == 1 ? "Your domain needs attention." : "Your domain isn't fully protected yet.";
        }

        var sentence = string.Create(CultureInfo.InvariantCulture, $"{protectedCount} of {statuses.Count} domains are fully protected.");
        return attentionCount switch
        {
            0 => sentence,
            1 => $"{sentence} 1 needs attention.",
            _ => string.Create(CultureInfo.InvariantCulture, $"{sentence} {attentionCount} need attention."),
        };
    }
}
