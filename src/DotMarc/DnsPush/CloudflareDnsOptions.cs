namespace DotMarc.DnsPush;

/// <summary>Optional — a deployment that never registers a Cloudflare OAuth client simply never
/// shows the "Push via Cloudflare" button (see CloudflareDnsPushProvider.IsConfigured).</summary>
public sealed class CloudflareDnsOptions
{
    public const string SectionName = "CloudflareDns";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
