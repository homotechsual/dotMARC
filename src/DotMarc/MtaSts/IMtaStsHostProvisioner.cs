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
}
