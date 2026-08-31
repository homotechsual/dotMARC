namespace DotMarc.Data;

/// <summary>The state of a Domain's MTA-STS policy hosting — see DotMarc.Ingestion.PollingService's
/// MTA-STS cycle for how each transition happens. NotConfigured is listed first so it is the enum's
/// (and therefore the database column's) default value: an existing domain from before this
/// feature, or one that hasn't opted in, is NotConfigured without needing any data migration.
/// Unlike DmarcCheckStatus, Failed is a real terminal-looking state here (still retried
/// automatically, just surfaced distinctly) rather than leaving the prior status untouched on
/// failure — a customer who just added a CNAME is actively watching this, not passively benefiting
/// from a background check, so silence would read as "nothing is happening" rather than "hosting
/// is broken."</summary>
public enum MtaStsStatus
{
    NotConfigured,
    PendingDns,
    PendingCertificate,
    Active,
    Failed
}
