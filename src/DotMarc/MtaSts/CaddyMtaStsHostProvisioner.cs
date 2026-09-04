namespace DotMarc.MtaSts;

/// <summary>Self-hosted deployments: nothing for the app to actively push. Caddy's on-demand TLS
/// (configured in the bundled Caddyfile) requests a certificate implicitly the first time a
/// request for mta-sts.&lt;domain&gt; succeeds through its "ask" callback
/// (GET /.well-known/mta-sts-ask) — which only returns success once DNS has already been verified
/// (see PollingService's MTA-STS cycle), so there's no separate provisioning step to trigger here.
/// Teardown is equally implicit: once MtaStsEnabled is false, "ask" starts 404ing and Caddy simply
/// stops renewing that certificate.</summary>
public sealed class CaddyMtaStsHostProvisioner : IMtaStsHostProvisioner
{
    public Task EnsureProvisionedAsync(string domainName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task TeardownAsync(string domainName, CancellationToken cancellationToken) => Task.CompletedTask;

    // Caddy's on-demand TLS never does DNS-based domain-ownership verification — there is no
    // per-deployment ID to surface here.
    public Task<string?> GetDomainVerificationIdAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
