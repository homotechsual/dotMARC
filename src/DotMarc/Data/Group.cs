namespace DotMarc.Data;

/// <summary>A user-defined container a domain can belong to — typically a client/owner in the
/// MSP use case this app is designed for, though a domain can belong to more than one Group at
/// once. Carries no access-control meaning on its own; that's the subject of a later design
/// cycle, not this one.</summary>
public sealed class Group
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Domain> Domains { get; set; } = [];
}
