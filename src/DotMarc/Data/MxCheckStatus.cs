namespace DotMarc.Data;

/// <summary>The result of the most recent MX DNS check for a Domain - see
/// DotMarc.Dns.MxDnsChecker. An explicit RFC 7505 null MX ("0 .") is NullMx - an intentional "this
/// domain sends but does not receive mail" policy, not a failure, but distinguishable from a normal
/// passing check since it also drives the domain's "null-routed" classification (see
/// DotMarc.Notifications.AlertingService and DashboardSummary, both of which key off
/// SpfCheckStatus.NullSpf specifically - NullMx alone does not flip alerting). NotChecked is listed
/// first so it is the enum's (and the database column's) default value.</summary>
public enum MxCheckStatus
{
    NotChecked,
    Ok,
    NullMx,
    MissingRecord,
    UnresolvableTarget
}
