using DotMarc.Data;
using DotMarc.DnsPush;

namespace DotMarc.Audit;

/// <summary>The audit entry for a successful DNS push: one field change per record, named by type and name.</summary>
public static class DnsPushAudit
{
    public static AuditEntry CreateEntry(AuditActor actor, Domain domain, string provider, IReadOnlyList<DnsRecordChange> changes) =>
        AuditLog.Create(actor, AuditEntryKind.Change, AuditActions.DnsPushed, AuditTarget.For(domain),
            $"Pushed {changes.Count} DNS {(changes.Count == 1 ? "record" : "records")} for {domain.Name} to {provider}",
            changes.Select(change => new AuditFieldChange($"{change.RecordType} {change.Name}", change.ExistingValue, change.DesiredValue)).ToList());
}
