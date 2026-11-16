namespace DotMarc.Data;

/// <summary>The result of the most recent DMARC DNS check for a Domain - see
/// DotMarc.Dns.DmarcDnsChecker for how each value is determined. NotChecked is listed first so it
/// is the enum's (and therefore the database column's) default value: an existing domain from
/// before this feature, or a domain that hasn't been checked yet, is NotChecked without needing
/// any data migration. MissingAuthorizationRecord is retained only so an existing database row
/// from before the DMARC-authorization split (see DmarcAuthorizationCheckStatus) still
/// deserializes correctly - DmarcDnsChecker.CheckAsync never emits it going forward; that check
/// now lives entirely in CheckAuthorizationAsync/DmarcAuthorizationCheckStatus.</summary>
public enum DmarcCheckStatus
{
    NotChecked,
    Ok,
    MissingOwnRecord,
    Misconfigured,
    MissingAuthorizationRecord,

    /// <summary>The record's rua= includes dotMARC's mailbox and also other addresses, within
    /// <see cref="DotMarc.Dns.DmarcDnsChecker.MaxReportAddresses"/> in total (typically a second tool being
    /// trialled alongside dotMARC). Healthy: shown as information and never alerts, but a DNS push is still
    /// offered so the other address can be removed.</summary>
    OtherReportAddresses,

    /// <summary>The record's rua= includes dotMARC's mailbox but lists more addresses than
    /// <see cref="DotMarc.Dns.DmarcDnsChecker.MaxReportAddresses"/>. Receivers may only send reports to
    /// the first few, so reports can stop reaching dotMARC. Counts as a fault and alerts.</summary>
    TooManyReportAddresses
}
