using MudBlazor;

namespace DotMarc.Data;

/// <summary>A curated, colored label a domain can carry (e.g. "primary") — many-to-many, used
/// for filtering on the Dashboard rather than ownership. Unlike Group, a Tag never implies
/// access to anything.</summary>
public sealed class Tag
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required Color Color { get; set; }
    public List<Domain> Domains { get; set; } = [];
}
