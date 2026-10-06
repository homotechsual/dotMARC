namespace DotMarc.MtaSts;

/// <summary>The host is still issuing the TLS certificate for a domain's mta-sts hostname, so it can't be bound yet. Not
/// a failure: issuing usually takes a few minutes, and the next MTA-STS check tries again.</summary>
public sealed class MtaStsCertificatePendingException(string hostname, Exception? innerException = null)
    : Exception($"Azure is still issuing the certificate for {hostname}. This usually takes a few minutes, and dotMARC retries automatically.", innerException)
{
    public string Hostname { get; } = hostname;
}
