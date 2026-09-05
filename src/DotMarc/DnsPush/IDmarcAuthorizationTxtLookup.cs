namespace DotMarc.DnsPush;

/// <summary>Unlike IDmarcTxtLookup/ITlsrptTxtLookup, which hardcode a fixed prefix onto a domain
/// name, the DMARC authorization record's name (RFC 7489 §7.1: &lt;domain&gt;._report._dmarc.
/// &lt;mailbox domain&gt;) is composed from two different domains — the client domain being
/// monitored and the deployment's own mailbox domain — so composing it is the caller's job; this
/// takes the already-built name.</summary>
public interface IDmarcAuthorizationTxtLookup
{
    Task<DnsRecordLookupResult> LookupAsync(string recordName, CancellationToken cancellationToken);
}
