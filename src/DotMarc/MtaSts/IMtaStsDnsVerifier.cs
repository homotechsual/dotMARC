namespace DotMarc.MtaSts;

public interface IMtaStsDnsVerifier
{
    Task<MtaStsDnsVerificationResult> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken);
}
