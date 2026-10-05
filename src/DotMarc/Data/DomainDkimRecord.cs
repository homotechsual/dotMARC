namespace DotMarc.Data;

/// <summary>The record a DKIM selector should have, as given by the mail platform: dotMARC pushes it and the DKIM
/// check compares DNS with it. One per selector in the domain's DkimSelectors.</summary>
public sealed class DomainDkimRecord
{
    public int Id { get; set; }
    public int DomainId { get; set; }
    public required string Selector { get; set; }
    public DkimRecordType RecordType { get; set; }
    public required string Value { get; set; }
}

/// <summary>A DKIM record as entered: a blank Value means the selector has no stored record.</summary>
public sealed record DkimRecordInput(string Selector, DkimRecordType Type, string Value);
