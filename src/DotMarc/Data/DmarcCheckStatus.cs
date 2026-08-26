namespace DotMarc.Data;

/// <summary>The result of the most recent DMARC DNS check for a Domain — see
/// DotMarc.Dns.DmarcDnsChecker for how each value is determined. NotChecked is listed first so it
/// is the enum's (and therefore the database column's) default value: an existing domain from
/// before this feature, or a domain that hasn't been checked yet, is NotChecked without needing
/// any data migration.</summary>
public enum DmarcCheckStatus
{
    NotChecked,
    Ok,
    MissingOwnRecord,
    Misconfigured,
    MissingAuthorizationRecord
}
