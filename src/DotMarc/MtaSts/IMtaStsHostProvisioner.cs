namespace DotMarc.MtaSts;

/// <summary>Provisions (or tears down) whatever the deployment target needs so that
/// mta-sts.&lt;domain&gt; actually serves over valid TLS, once DNS has been verified. See
/// CaddyMtaStsHostProvisioner (self-hosted, no-op — Caddy's own on-demand TLS does the work
/// implicitly) and AzureMtaStsHostProvisioner (Container Apps custom domain + managed
/// certificate).</summary>
public interface IMtaStsHostProvisioner
{
    Task EnsureProvisionedAsync(string domainName, CancellationToken cancellationToken);
    Task TeardownAsync(string domainName, CancellationToken cancellationToken);

    /// <summary>The value Azure Container Apps needs at asuid.&lt;custom-domain&gt; TXT before it
    /// will bind that custom domain — a property of the Container App resource itself, so it is
    /// the same value for every domain this deployment hosts. Null on providers (Caddy) that have
    /// no such concept.</summary>
    Task<string?> GetDomainVerificationIdAsync(CancellationToken cancellationToken);
}
