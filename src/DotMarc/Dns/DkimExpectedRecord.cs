using DotMarc.Data;

namespace DotMarc.Dns;

public sealed record DkimExpectedRecord(string Selector, DkimRecordType Type, string Value);
