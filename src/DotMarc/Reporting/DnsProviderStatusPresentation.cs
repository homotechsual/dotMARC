using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DetectedDnsProvider to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist - same shared-presentation-logic precedent as DmarcStatusPresentation.
/// Unknown gets Color.Warning rather than the neutral default - it specifically means no auto-push
/// option is available, worth flagging the same way a missing record is.</summary>
public static class DnsProviderStatusPresentation
{
    public static Color GetColor(DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare or DetectedDnsProvider.AzureDns or DetectedDnsProvider.GoogleCloudDns => Color.Success,
        DetectedDnsProvider.Unknown => Color.Warning,
        DetectedDnsProvider.NotChecked => Color.Default,
        // Recognized but with no auto-push integration - worth distinguishing from both "all set"
        // (Success) and "couldn't identify it at all" (Warning).
        _ => Color.Info
    };

    public static string GetLabel(DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare => "Cloudflare",
        DetectedDnsProvider.AzureDns => "Azure DNS",
        DetectedDnsProvider.GoogleCloudDns => "Google Cloud DNS",
        DetectedDnsProvider.Microsoft365 => "Microsoft 365",
        DetectedDnsProvider.AmazonRoute53 => "Amazon Route 53",
        DetectedDnsProvider.GoDaddy => "GoDaddy",
        DetectedDnsProvider.Namecheap => "Namecheap",
        DetectedDnsProvider.DigitalOcean => "DigitalOcean",
        DetectedDnsProvider.Ovh => "OVH",
        DetectedDnsProvider.Gandi => "Gandi",
        DetectedDnsProvider.Ns1 => "NS1",
        DetectedDnsProvider.DnsMadeEasy => "DNS Made Easy",
        DetectedDnsProvider.Vercel => "Vercel",
        DetectedDnsProvider.Unknown => "Not recognised",
        _ => "Not checked yet"
    };
}
