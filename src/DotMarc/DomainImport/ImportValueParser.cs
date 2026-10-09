using System.Globalization;
using DotMarc.Data;

namespace DotMarc.DomainImport;

/// <summary>Turns one cell into a value. A blank cell is null. A value that doesn't make sense is also null, with a
/// problem saying why, so the row still imports without it.</summary>
internal static class ImportValueParser
{
    public const int MaximumMaxAgeSeconds = 31_557_600;

    public static NameListCell? NameList(string cell)
    {
        var entries = SplitList(cell);
        if (entries.Count == 0)
        {
            return null;
        }

        var removals = entries.Where(entry => entry.StartsWith('-')).Select(entry => entry[1..].Trim()).Where(name => name.Length > 0).ToList();
        var names = entries.Where(entry => !entry.StartsWith('-')).ToList();
        return new NameListCell(names, removals);
    }

    public static string? Text(string cell) => cell.Length == 0 ? null : cell;

    public static bool? Monitored(string cell, List<string> problems) => cell.ToLowerInvariant() switch
    {
        "" => null,
        "yes" or "true" or "1" => true,
        "no" or "false" or "0" => false,
        _ => Refuse<bool?>(problems, $"Monitored \"{cell}\" should be yes or no, so it was left as it is.")
    };

    public static IReadOnlyList<string>? DkimSelectors(string cell, List<string> problems)
    {
        var selectors = SplitList(cell).Select(selector => selector.ToLowerInvariant()).ToList();
        if (selectors.Count == 0)
        {
            return null;
        }

        var invalid = selectors.FirstOrDefault(selector => !selector.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
        return invalid is null
            ? selectors
            : Refuse<IReadOnlyList<string>?>(problems, $"DKIM selector \"{invalid}\" can only contain letters, digits, hyphens, underscores and dots, so the selectors were left as they are.");
    }

    public static MtaStsImportMode? MtaStsMode(string cell, List<string> problems) => cell.ToLowerInvariant() switch
    {
        "" => null,
        "off" => MtaStsImportMode.Off,
        "none" => MtaStsImportMode.None,
        "testing" => MtaStsImportMode.Testing,
        "enforce" => MtaStsImportMode.Enforce,
        _ => Refuse<MtaStsImportMode?>(problems, $"MTA-STS mode \"{cell}\" should be off, none, testing or enforce, so it was left as it is.")
    };

    public static IReadOnlyList<string>? MxHosts(string cell, List<string> problems)
    {
        var hosts = SplitList(cell);
        if (hosts.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>();
        foreach (var host in hosts)
        {
            // MTA-STS allows a leading wildcard label, such as *.mail.contoso.example.
            var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
            if (!DomainNameValidator.TryNormalize(wildcard ? host[2..] : host, out var hostName))
            {
                return Refuse<IReadOnlyList<string>?>(problems, $"MX host \"{host}\" isn't a host name, so the MX hosts were left as they are.");
            }

            normalized.Add(wildcard ? "*." + hostName : hostName);
        }

        return normalized;
    }

    public static int? MaxAge(string cell, List<string> problems)
    {
        if (cell.Length == 0)
        {
            return null;
        }

        return int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= MaximumMaxAgeSeconds
            ? seconds
            : Refuse<int?>(problems, $"MTA-STS max age \"{cell}\" should be a number of seconds from 1 to {MaximumMaxAgeSeconds:N0}, so it was left as it is.");
    }

    private static List<string> SplitList(string cell) =>
        cell.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static T Refuse<T>(List<string> problems, string problem)
    {
        problems.Add(problem);
        return default!;
    }
}
