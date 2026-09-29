using System.Globalization;

namespace DotMarc.Audit;

/// <summary>Builds the field changes for an audit entry, keeping only fields whose value actually differs.</summary>
public sealed class AuditChanges
{
    private readonly List<AuditFieldChange> _changes = [];

    public bool Any => _changes.Count > 0;

    public IReadOnlyList<AuditFieldChange> Items => _changes;

    public AuditChanges Field<T>(string name, T oldValue, T newValue)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            _changes.Add(new AuditFieldChange(name, Format(oldValue), Format(newValue)));
        }

        return this;
    }

    /// <summary>For a set of names, such as a domain's groups or a role's permissions: order doesn't matter, and the
    /// values are recorded sorted and comma-separated, or "None" when empty.</summary>
    public AuditChanges Set(string name, IEnumerable<string> oldValues, IEnumerable<string> newValues)
    {
        var oldSet = oldValues.ToHashSet(StringComparer.Ordinal);
        var newSet = newValues.ToHashSet(StringComparer.Ordinal);
        if (!oldSet.SetEquals(newSet))
        {
            _changes.Add(new AuditFieldChange(name, Describe(oldSet), Describe(newSet)));
        }

        return this;
    }

    /// <summary>Records that a secret changed, never its value.</summary>
    public AuditChanges Secret(string name, bool changed)
    {
        if (changed)
        {
            _changes.Add(new AuditFieldChange(name, null, null, Secret: true));
        }

        return this;
    }

    private static string Describe(IEnumerable<string> values)
    {
        var sorted = values.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return sorted.Count == 0 ? "None" : string.Join(", ", sorted);
    }

    private static string? Format<T>(T value) => value switch
    {
        null => null,
        bool flag => flag ? "Yes" : "No",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
