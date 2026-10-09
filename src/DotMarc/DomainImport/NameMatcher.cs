using DotMarc.Psa;

namespace DotMarc.DomainImport;

/// <summary>Finds which existing group, tag or Halo client a name in an import means, and suggests the closest ones when
/// it's unknown, to catch typos.</summary>
public static class NameMatcher
{
    private const int MaximumEdits = 2;
    private const int ShortestNameForTypos = 4;

    public static string? FindExisting(string name, IEnumerable<string> existingNames) =>
        existingNames.FirstOrDefault(existing => string.Equals(existing, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when two names are the same name written differently: case, punctuation, spacing, or a company
    /// suffix such as "Ltd".</summary>
    public static bool IsSameName(string first, string second)
    {
        var firstKey = PsaCompanySuggestions.LooseKey(first);
        return firstKey.Length > 0 && firstKey == PsaCompanySuggestions.LooseKey(second);
    }

    /// <summary>Close existing names, best first: the same name written differently (case, punctuation, "Ltd"), then
    /// names within one or two letters of it. Names shorter than four letters only match the first way, or every short
    /// name would look like a typo of every other.</summary>
    public static IReadOnlyList<string> Suggest(string name, IEnumerable<string> existingNames, int maximum = 3)
    {
        var key = PsaCompanySuggestions.LooseKey(name);
        var lowered = name.Trim().ToLowerInvariant();

        return existingNames
            .Select(existing => (Name: existing, Score: Score(key, lowered, existing)))
            .Where(candidate => candidate.Score is not null)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .Select(candidate => candidate.Name)
            .ToList();
    }

    private static int? Score(string key, string lowered, string existing)
    {
        if (key.Length > 0 && key == PsaCompanySuggestions.LooseKey(existing))
        {
            return 0;
        }

        var existingLowered = existing.ToLowerInvariant();
        if (Math.Min(lowered.Length, existingLowered.Length) < ShortestNameForTypos)
        {
            return null;
        }

        var edits = EditDistance(lowered, existingLowered);
        return edits <= MaximumEdits ? edits : null;
    }

    /// <summary>The number of single-letter inserts, deletes or changes that turn one name into the other.</summary>
    internal static int EditDistance(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        var current = new int[second.Length + 1];
        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            current[0] = firstIndex;
            for (var secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                var substitution = previous[secondIndex - 1] + (first[firstIndex - 1] == second[secondIndex - 1] ? 0 : 1);
                current[secondIndex] = Math.Min(substitution, Math.Min(previous[secondIndex] + 1, current[secondIndex - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[second.Length];
    }
}
