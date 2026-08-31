namespace DotMarc.MtaSts;

/// <summary>Unlike GraphOptions, this section is optional — a deployment that hosts no MTA-STS
/// policies needs neither value set. Only validated (see Program.cs) when at least one Domain
/// actually has MtaStsEnabled.</summary>
public sealed class MtaStsOptions
{
    public const string SectionName = "MtaSts";

    /// <summary>The hostname a customer's mta-sts.&lt;domain&gt; CNAME must resolve to for this
    /// deployment — the Azure Container App's own FQDN, or whatever hostname the self-hosted
    /// Caddy instance answers on.</summary>
    public string? HostingHostname { get; set; }

    /// <summary>Which IMtaStsHostProvisioner implementation to use: "Caddy" (self-hosted,
    /// on-demand TLS — the default) or "Azure" (Container Apps custom domain + managed
    /// certificate via the Resource Manager API).</summary>
    public string Provisioner { get; set; } = "Caddy";

    // The remaining properties are only read by AzureMtaStsHostProvisioner (Provisioner: "Azure").

    public string? AzureSubscriptionId { get; set; }
    public string? AzureResourceGroupName { get; set; }
    public string? AzureContainerAppName { get; set; }
    public string? AzureManagedEnvironmentName { get; set; }
}
