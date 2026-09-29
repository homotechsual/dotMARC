using System.Globalization;

namespace DotMarc.Audit;

/// <summary>Writes audit entries as CSV, one row at a time, so a large export never sits in memory.</summary>
public static class AuditCsv
{
    private static readonly string[] Header = ["Time (UTC)", "Kind", "Who", "Email", "Action", "Target type", "Target", "Summary", "Changes"];

    public static async Task WriteAsync(TextWriter writer, IAsyncEnumerable<AuditEntry> entries, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(string.Join(',', Header.Select(Cell))).ConfigureAwait(false);
        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var row = new[]
            {
                entry.OccurredUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Kind.ToString(),
                entry.ActorName,
                entry.ActorEmail,
                entry.Action,
                entry.TargetType,
                entry.TargetName,
                entry.Summary,
                FormatChanges(entry.Changes),
            };
            await writer.WriteLineAsync(string.Join(',', row.Select(Cell))).ConfigureAwait(false);
        }
    }

    public static string FormatChanges(IEnumerable<AuditFieldChange> changes) =>
        string.Join("; ", changes.Select(change => change.Secret
            ? $"{change.Field}: changed (value not recorded)"
            : $"{change.Field}: {change.Old ?? "(none)"} -> {change.New ?? "(none)"}"));

    /// <summary>One CSV cell. A value a spreadsheet would run as a formula gets a leading apostrophe, and a value
    /// with a comma, quote or line break is quoted with its quotes doubled.</summary>
    private static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]))
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}
