namespace DotMarc.MtaSts;

/// <summary>Confirms mta-sts.&lt;domain&gt; is actually serving the expected policy over HTTPS —
/// the same check works for both deployment targets (see the design spec's "Onboarding state
/// machine" section): on Caddy, this request is itself what triggers on-demand certificate
/// issuance the first time it succeeds; on Azure, it's a pure health check confirming the
/// ARM-provisioned binding has gone live. No separate webhook or ARM-status poll needed.</summary>
public interface IMtaStsServingVerifier
{
    Task<bool> IsServingCorrectlyAsync(string domainName, string expectedPolicyText, CancellationToken cancellationToken);
}
