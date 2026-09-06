namespace DotMarc.Data;

/// <summary>The result of the most recent MX DNS check for a Domain - see
/// DotMarc.Dns.MxDnsChecker. An explicit RFC 7505 null MX ("0 .") counts as Ok - it's an
/// intentional "this domain sends but does not receive mail" policy, not a failure. NotChecked is
/// listed first so it is the enum's (and the database column's) default value.</summary>
public enum MxCheckStatus
{
    NotChecked,
    Ok,
    MissingRecord,
    UnresolvableTarget
}
