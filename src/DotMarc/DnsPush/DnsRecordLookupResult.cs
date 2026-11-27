namespace DotMarc.DnsPush;

/// <summary>The result of looking up a record's current live DNS state. DirectValue is the final
/// resolved value (same as today's plain string result) - non-null whenever something resolves,
/// regardless of whether a CNAME hop happened first. DelegatedToCname is set only when the record
/// at the expected name is itself a CNAME (not a direct TXT record) - e.g. a domain's _dmarc TXT
/// delegated to a third-party DMARC monitoring service via CNAME. A plain TXT query transparently
/// follows CNAMEs and would otherwise lose this distinction. TxtValues is every TXT value at the
/// name, each joined from its quoted segments (empty when none), for builders that must tell
/// several values apart.</summary>
public sealed record DnsRecordLookupResult(string? DirectValue, string? DelegatedToCname)
{
    public IReadOnlyList<string> TxtValues { get; init; } = [];
}
