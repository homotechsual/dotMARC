namespace DotMarc.Data;

/// <summary>The most recently detected DNS provider for a Domain - see
/// DotMarc.DnsPush.IDnsProviderDetector. NotChecked is listed first so it is the enum's (and the
/// database column's) default value; Unknown means the check ran but no known provider's NS suffix
/// matched at the resolved zone - still meaningfully different from never having checked at all.</summary>
public enum DetectedDnsProvider
{
    NotChecked,
    Unknown,
    Cloudflare,
    AzureDns,
    GoogleCloudDns
}
