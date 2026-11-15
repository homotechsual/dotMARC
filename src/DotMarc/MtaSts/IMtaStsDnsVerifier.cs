namespace DotMarc.MtaSts;

public interface IMtaStsDnsVerifier
{
    Task<MtaStsDnsVerification> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken);
}
