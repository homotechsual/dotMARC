using DotMarc.Data;
using DotMarc.DnsPush;

namespace DotMarc.Audit;

/// <summary>The audit entry for a DNS push, one field change per record, named by type and name. A push that
/// failed can still have changed the live zone: a replace deletes the old record before creating the new one, and
/// every provider stops at the first failing record, leaving the earlier ones live. So those failures are recorded
/// too. Only a push that never reached the zone records nothing.</summary>
public static class DnsPushAudit
{
    public static AuditEntry? CreateEntry(AuditActor actor, Domain domain, string provider, IReadOnlyList<DnsRecordChange> changes, DnsPushOutcome outcome)
    {
        var (action, summary) = outcome switch
        {
            DnsPushOutcome.Pushed => (AuditActions.DnsPushed,
                $"Pushed {changes.Count} DNS {(changes.Count == 1 ? "record" : "records")} for {domain.Name} to {provider}"),
            DnsPushOutcome.ReplaceFailedAfterDelete => (AuditActions.DnsPushFailed,
                $"A DNS push for {domain.Name} to {provider} failed after deleting the old record, so the name may now have no record"),
            DnsPushOutcome.ProviderError => (AuditActions.DnsPushFailed,
                $"A DNS push for {domain.Name} to {provider} failed partway, so some of these records may already be live"),
            _ => (null, null)
        };

        if (action is null)
        {
            return null;
        }

        // The provider's error text isn't included: it adds nothing to "what changed" and is already in the server log.
        return AuditLog.Create(actor, AuditEntryKind.Change, action, AuditTarget.For(domain), summary!,
            changes.Select(change => new AuditFieldChange(
                change.Kind == DnsRecordChangeKind.ReplaceTxtValues ? $"SPF {change.Name}" : $"{change.RecordType} {change.Name}",
                change.ExistingValue, change.DesiredValue)).ToList());
    }
}
