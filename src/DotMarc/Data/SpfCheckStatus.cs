namespace DotMarc.Data;

/// <summary>The result of the most recent SPF DNS check for a Domain — see
/// DotMarc.Dns.SpfDnsChecker. Only checks presence, record uniqueness, and the v=spf1 prefix; does
/// not validate the 10-DNS-lookup limit (RFC 7208) or the mechanism chain. NotChecked is listed
/// first so it is the enum's (and the database column's) default value.</summary>
public enum SpfCheckStatus
{
    NotChecked,
    Ok,
    MissingRecord,
    MultipleRecords,
    Misconfigured
}
