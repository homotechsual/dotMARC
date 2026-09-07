using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>Maps a detected provider to the {provider} route segment IDnsPushProvider.ProviderKey
/// values use ("cloudflare"/"azure-dns") - see IDnsPushProvider.ProviderKey's own doc comment for
/// the paired convention this centralizes. Returns null for Unknown/NotChecked, meaning "no
/// configured push provider matches."</summary>
public static class DetectedDnsProviderExtensions
{
    public static string? ToProviderKey(this DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare => "cloudflare",
        DetectedDnsProvider.AzureDns => "azure-dns",
        DetectedDnsProvider.GoogleCloudDns => "google-cloud-dns",
        _ => null
    };
}
