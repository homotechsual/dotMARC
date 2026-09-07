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
        _ => Color.Default
    };

    public static string GetLabel(DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare => "Cloudflare",
        DetectedDnsProvider.AzureDns => "Azure DNS",
        DetectedDnsProvider.GoogleCloudDns => "Google Cloud DNS",
        DetectedDnsProvider.Unknown => "Not recognized",
        _ => "Not checked yet"
    };
}
