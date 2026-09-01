namespace DotMarc.DnsPush;

/// <summary>Optional — a deployment that never registers this Entra app simply never shows the
/// "Push via Azure DNS" button. A THIRD, separate app registration from the existing mailbox and
/// dashboard ones (see getting-started.mdx) — never reuse an app registration across purposes.</summary>
public sealed class AzureDnsOptions
{
    public const string SectionName = "AzureDns";

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
