using System.Globalization;
using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;

namespace DotMarc.Portal;

public sealed record HealthRow(string Name, string Label, Color Colour);

/// <summary>How the portal and client reports describe a domain to clients, so both say it the same way.</summary>
public static class PortalWording
{
    public static IReadOnlyList<HealthRow> HealthRows(Domain domain)
    {
        var rows = new List<HealthRow>
        {
            new("DMARC", DmarcStatusPresentation.GetLabel(domain.DmarcCheckStatus), DmarcStatusPresentation.GetColor(domain.DmarcCheckStatus)),
            new("DMARC reporting", DmarcAuthorizationStatusPresentation.GetLabel(domain.DmarcAuthorizationCheckStatus), DmarcAuthorizationStatusPresentation.GetColor(domain.DmarcAuthorizationCheckStatus)),
            new("SPF", SpfStatusPresentation.GetLabel(domain.SpfCheckStatus), SpfStatusPresentation.GetColor(domain.SpfCheckStatus)),
            new("MX", MxStatusPresentation.GetLabel(domain.MxCheckStatus), MxStatusPresentation.GetColor(domain.MxCheckStatus)),
            new("DKIM", DkimStatusPresentation.GetLabel(domain.DkimCheckStatus), DkimStatusPresentation.GetColor(domain.DkimCheckStatus)),
            new("TLS reporting", TlsrptStatusPresentation.GetLabel(domain.TlsrptCheckStatus), TlsrptStatusPresentation.GetColor(domain.TlsrptCheckStatus)),
        };
        if (domain.MtaStsStatus != MtaStsStatus.NotConfigured)
        {
            rows.Add(new("MTA-STS", MtaStsStatusPresentation.GetLabel(domain.MtaStsStatus), MtaStsStatusPresentation.GetColor(domain.MtaStsStatus)));
        }

        return rows;
    }

    public static string PolicySentence(Domain domain)
    {
        if (domain.DmarcPolicy is not { } policy)
        {
            return "No DMARC policy was found, so receivers decide for themselves what to do with mail that fails.";
        }

        var main = policy switch
        {
            DmarcPolicyLevel.Reject => "Mail that fails DMARC is rejected",
            DmarcPolicyLevel.Quarantine => "Mail that fails DMARC is sent to spam",
            _ => "Mail that fails DMARC is only reported, not blocked",
        };
        var percent = policy != DmarcPolicyLevel.None && domain.DmarcPercent is { } share && share < 100
            ? string.Create(CultureInfo.InvariantCulture, $" ({share}% of it)")
            : "";
        var subdomains = domain.DmarcSubdomainPolicy is { } subdomainPolicy && subdomainPolicy != policy
            ? subdomainPolicy switch
            {
                DmarcPolicyLevel.Reject => " Subdomains: rejected.",
                DmarcPolicyLevel.Quarantine => " Subdomains: sent to spam.",
                _ => " Subdomains: only reported.",
            }
            : "";
        return $"{main}{percent}.{subdomains}";
    }
}
