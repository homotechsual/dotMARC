namespace DotMarc.DnsPush;

/// <summary>Replace deletes whatever record currently exists at Name (of ExistingRecordType) and
/// creates a new RecordType record in its place — used when the existing record is a CNAME
/// delegating elsewhere (DNS doesn't allow a CNAME to coexist with any other record type at the
/// same name, so there is no in-place "merge" for this case, only delete-then-create).</summary>
public enum DnsRecordChangeKind { Create, Merge, Replace }

/// <summary>One DNS record change to push, independent of which provider ends up handling it.
/// ExistingValue is set for Kind == Merge (the pushed value replaces, not appends to, whatever's
/// currently live — see DmarcRuaMerge for how that value is actually built) and for Kind ==
/// Replace (the CNAME target being deleted, for display/logging). ExistingRecordType is set only
/// for Kind == Replace — the record type being deleted (e.g. "CNAME"), which differs from
/// RecordType (the type being created, e.g. "TXT").</summary>
public sealed record DnsRecordChange(
    DnsRecordChangeKind Kind,
    string RecordType,
    string Name,
    string DesiredValue,
    string? ExistingValue,
    string ZoneName,
    string? ExistingRecordType = null);
