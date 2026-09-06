namespace DotMarc.Data;

/// <summary>The result of the most recent RFC 7489 §7.1 DMARC authorization-record check for a
/// Domain — see DotMarc.Dns.DmarcDnsChecker.CheckAuthorizationAsync. Split out from
/// DmarcCheckStatus so the own-record check and this one can be shown (and independently
/// corrected) regardless of the other's state; the old DmarcCheckStatus.MissingAuthorizationRecord
/// value stays defined for backward compatibility with existing rows but is never emitted again.
/// NotChecked is listed first so it is the enum's (and the database column's) default value.</summary>
public enum DmarcAuthorizationCheckStatus
{
    NotChecked,
    NotApplicable,
    Ok,
    Missing
}
