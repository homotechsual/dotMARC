using System.Text;

namespace DotMarc.Psa;

/// <summary>Works out which PSA companies a dotMARC group could be linked to, and which companies have no group yet.
/// Names are compared loosely, because a PSA company is usually the full legal name ("Compute (Bridgend) Limited")
/// while a group is whatever the MSP typed ("Compute Bridgend").</summary>
public static class PsaCompanySuggestions
{
    /// <summary>Halo's built-in "Unknown" client, which tickets fall back to. It is not a customer to make a group for.</summary>
    public const string HaloUnknownClientId = "1";

    private static readonly HashSet<string> CompanySuffixes = new(StringComparer.Ordinal)
    {
        "limited", "ltd", "llc", "llp", "inc", "incorporated", "plc", "cyf", "cic", "corp", "corporation", "co", "company"
    };

    /// <summary>The companies in one PSA that no group covers yet: not already linked to a group, and with no group whose name
    /// matches. These are the ones to offer to create a group for.</summary>
    public static IReadOnlyList<PsaCompany> CompaniesWithoutGroup(PsaKind psa, IEnumerable<PsaCompany> clients, IEnumerable<GroupSummary> groups)
    {
        var groupList = groups.ToList();
        var linkedClientIds = groupList.Where(group => group.CompanyId is not null).Select(group => group.CompanyId!).ToHashSet();

        return clients
            .Where(client => !IsHaloUnknown(psa, client)
                && !string.IsNullOrWhiteSpace(client.Name)
                && !linkedClientIds.Contains(client.Id)
                && !groupList.Any(group => NamesMatch(group.Name, client.Name)))
            .OrderBy(client => client.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The companies a group of this name probably refers to, best match first: an exact match once
    /// punctuation and suffixes like "Limited" are ignored, then clients whose name starts with the group's name.</summary>
    public static IReadOnlyList<PsaCompany> SuggestCompaniesForGroup(PsaKind psa, string groupName, IEnumerable<PsaCompany> clients)
    {
        var groupTokens = Tokenise(groupName);
        if (groupTokens.Count == 0)
        {
            return [];
        }

        var candidates = clients
            .Where(client => !IsHaloUnknown(psa, client))
            .Select(client => (Client: client, Tokens: Tokenise(client.Name)))
            .Where(candidate => candidate.Tokens.Count > 0)
            .ToList();

        var exact = candidates.Where(candidate => candidate.Tokens.SequenceEqual(groupTokens, StringComparer.Ordinal)).ToList();
        var prefix = candidates
            .Except(exact)
            .Where(candidate => IsTokenPrefix(groupTokens, candidate.Tokens) || IsTokenPrefix(candidate.Tokens, groupTokens));

        return exact.Concat(prefix).Select(candidate => candidate.Client).ToList();
    }

    private static bool IsHaloUnknown(PsaKind psa, PsaCompany company) => psa == PsaKind.HaloPsa && company.Id == HaloUnknownClientId;

    public static bool NamesMatch(string groupName, string clientName)
    {
        var groupTokens = Tokenise(groupName);
        var clientTokens = Tokenise(clientName);
        if (groupTokens.Count == 0 || clientTokens.Count == 0)
        {
            return false;
        }

        return IsTokenPrefix(groupTokens, clientTokens) || IsTokenPrefix(clientTokens, groupTokens);
    }

    /// <summary>A name reduced for loose comparison: lower-cased words, punctuation dropped, trailing company suffixes
    /// such as "Limited" removed. Two names with the same key are the same name written differently.</summary>
    public static string LooseKey(string name) => string.Join(' ', Tokenise(name));

    /// <summary>True when <paramref name="shorter"/> is the start of <paramref name="longer"/> (or the same), word for word.</summary>
    private static bool IsTokenPrefix(IReadOnlyList<string> shorter, IReadOnlyList<string> longer)
    {
        if (shorter.Count > longer.Count)
        {
            return false;
        }

        for (var index = 0; index < shorter.Count; index++)
        {
            if (!string.Equals(shorter[index], longer[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Lower-cased words with punctuation dropped ("&amp;" becomes "and") and trailing company suffixes removed.</summary>
    private static List<string> Tokenise(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            builder.Append(character switch
            {
                '&' => " and ",
                _ when char.IsLetterOrDigit(character) => character,
                _ => ' '
            });
        }

        var tokens = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && CompanySuffixes.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return tokens;
    }
}

/// <summary>What the matching needs to know about a group.</summary>
public sealed record GroupSummary(string Name, string? CompanyId);
