namespace DotMarc.Components.Shared;

/// <summary>One choice in a <see cref="SearchableSelect{TValue}"/> or <see cref="SearchableMultiSelect{TValue}"/>:
/// the value it stands for and the text shown and searched.</summary>
public sealed record SelectOption<TValue>(TValue Value, string Text);

/// <summary>The matching both searchable dropdowns share, so a search behaves the same in each.</summary>
public static class SelectSearch
{
    /// <summary>The options whose text contains the search, ignoring case and any spaces around it, in their
    /// original order. An empty search matches everything.</summary>
    public static IEnumerable<SelectOption<TValue>> Filter<TValue>(IEnumerable<SelectOption<TValue>> options, string? search)
    {
        var trimmed = search?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? options
            : options.Where(option => option.Text.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
