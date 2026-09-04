namespace DotMarc.DnsPush;

public enum DnsRecordChangeKind { Create, Merge }

/// <summary>One DNS record change to push, independent of which provider ends up handling it.
/// ExistingValue is set only for Kind == Merge, where the pushed value replaces (not appends to)
/// whatever's currently live — see DmarcRuaMerge for how that value is actually built.</summary>
public sealed record DnsRecordChange(
    DnsRecordChangeKind Kind,
    string RecordType,
    string Name,
    string DesiredValue,
    string? ExistingValue,
    string ZoneName);
