namespace DotMarc.Data;

/// <summary>The result of the most recent SPF DNS check for a Domain - see
/// DotMarc.Dns.SpfDnsChecker. Only checks presence, record uniqueness, the v=spf1 prefix, and the
/// "no senders authorized" null pattern (v=spf1 -all, exactly); does not validate the 10-DNS-lookup
/// limit (RFC 7208) or the mechanism chain. NullSpf drives the domain's "null-routed"
/// classification everywhere it's used (AlertingService, DashboardSummary) - it is the single
/// source of truth for that concept; there is no separate stored flag. NotChecked is listed first
/// so it is the enum's (and the database column's) default value.</summary>
public enum SpfCheckStatus
{
    NotChecked,
    Ok,
    NullSpf,
    MissingRecord,
    MultipleRecords,
    Misconfigured
}
