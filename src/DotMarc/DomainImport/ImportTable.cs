namespace DotMarc.DomainImport;

public enum ImportColumn { Domain, Groups, Tags, HaloClient, Monitored, DkimSelectors, MtaStsMode, MtaStsMxHosts, MtaStsMaxAge }

public enum MtaStsImportMode { Off, None, Testing, Enforce }

/// <summary>A groups or tags cell: names to add, and names written as <c>-Name</c> to remove.</summary>
public sealed record NameListCell(IReadOnlyList<string> Names, IReadOnlyList<string> Removals)
{
    /// <summary>Two rows for the same domain: their names and removals combined.</summary>
    public static NameListCell? Combine(NameListCell? first, NameListCell? second) =>
        (first, second) switch
        {
            (null, _) => second,
            (_, null) => first,
            _ => new NameListCell(
                first.Names.Concat(second.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                first.Removals.Concat(second.Removals).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        };
}

/// <summary>One row's values. Null means the cell was blank, the column is missing, or the value was refused, in which
/// case <see cref="Problems"/> says why.</summary>
public sealed record ImportTableRow(
    int LineNumber,
    string RawDomain,
    NameListCell? Groups,
    NameListCell? Tags,
    string? HaloClient,
    bool? Monitored,
    IReadOnlyList<string>? DkimSelectors,
    MtaStsImportMode? MtaStsMode,
    IReadOnlyList<string>? MtaStsMxHosts,
    int? MtaStsMaxAgeSeconds,
    IReadOnlyList<string> Problems);

/// <summary>The input as typed rows: which columns it has, the rows, and warnings about the columns.</summary>
public sealed record ImportTable(IReadOnlySet<ImportColumn> Columns, IReadOnlyList<ImportTableRow> Rows, IReadOnlyList<string> Warnings)
{
    public const int MaximumDataRows = 1000;

    private static readonly ImportColumn[] PositionalOrder = Enum.GetValues<ImportColumn>();

    private static readonly Dictionary<string, ImportColumn> HeaderNames = new(StringComparer.Ordinal)
    {
        ["domain"] = ImportColumn.Domain,
        ["groups"] = ImportColumn.Groups, ["group"] = ImportColumn.Groups,
        ["tags"] = ImportColumn.Tags, ["tag"] = ImportColumn.Tags,
        ["haloclient"] = ImportColumn.HaloClient, ["halo"] = ImportColumn.HaloClient,
        ["monitored"] = ImportColumn.Monitored,
        ["dkimselectors"] = ImportColumn.DkimSelectors, ["dkim"] = ImportColumn.DkimSelectors,
        ["mtastsmode"] = ImportColumn.MtaStsMode, ["mtasts"] = ImportColumn.MtaStsMode,
        ["mtastsmxhosts"] = ImportColumn.MtaStsMxHosts, ["mxhosts"] = ImportColumn.MtaStsMxHosts,
        ["mtastsmaxage"] = ImportColumn.MtaStsMaxAge, ["maxage"] = ImportColumn.MtaStsMaxAge,
    };

    public static ImportTable FromRows(IReadOnlyList<ImportRow> rows)
    {
        var warnings = new List<string>();
        // A header row names its columns, one of which is "domain". Data never holds that, since a domain has a dot.
        var hasHeader = rows.Count > 0 && rows[0].Cells.Any(cell => HeaderKey(cell) == "domain");
        var dataRows = hasHeader ? rows.Skip(1).ToList() : rows.ToList();

        if (dataRows.Count == 0)
        {
            throw new ImportInputException("There's nothing to import. Paste a list of domains, or choose a file.");
        }

        if (dataRows.Count > MaximumDataRows)
        {
            throw new ImportInputException($"There are {dataRows.Count:N0} rows, and an import can have at most {MaximumDataRows:N0}. Split it into smaller imports.");
        }

        // Which input column holds each ImportColumn.
        var columnIndexes = new Dictionary<ImportColumn, int>();
        if (hasHeader)
        {
            for (var index = 0; index < rows[0].Cells.Count; index++)
            {
                var header = rows[0].Cells[index];
                if (header.Length == 0)
                {
                    continue;
                }

                if (!HeaderNames.TryGetValue(HeaderKey(header), out var column))
                {
                    warnings.Add($"The column \"{header}\" isn't one dotMARC knows, so it was ignored.");
                }
                else if (!columnIndexes.TryAdd(column, index))
                {
                    warnings.Add($"The column \"{header}\" appears more than once, so only the first was used.");
                }
            }
        }
        else
        {
            var widest = dataRows.Max(row => row.Cells.Count);
            for (var index = 0; index < Math.Min(widest, PositionalOrder.Length); index++)
            {
                columnIndexes[PositionalOrder[index]] = index;
            }

            if (widest > PositionalOrder.Length)
            {
                warnings.Add($"Without a header row only the first {PositionalOrder.Length} columns are read, so the rest were ignored.");
            }
        }

        var typedRows = dataRows.Select(row => ToTableRow(row, columnIndexes)).ToList();
        return new ImportTable(columnIndexes.Keys.ToHashSet(), typedRows, warnings);
    }

    private static ImportTableRow ToTableRow(ImportRow row, Dictionary<ImportColumn, int> columnIndexes)
    {
        var problems = new List<string>();
        string Cell(ImportColumn column) =>
            columnIndexes.TryGetValue(column, out var index) && index < row.Cells.Count ? row.Cells[index] : "";

        return new ImportTableRow(
            row.LineNumber,
            Cell(ImportColumn.Domain),
            ImportValueParser.NameList(Cell(ImportColumn.Groups)),
            ImportValueParser.NameList(Cell(ImportColumn.Tags)),
            ImportValueParser.Text(Cell(ImportColumn.HaloClient)),
            ImportValueParser.Monitored(Cell(ImportColumn.Monitored), problems),
            ImportValueParser.DkimSelectors(Cell(ImportColumn.DkimSelectors), problems),
            ImportValueParser.MtaStsMode(Cell(ImportColumn.MtaStsMode), problems),
            ImportValueParser.MxHosts(Cell(ImportColumn.MtaStsMxHosts), problems),
            ImportValueParser.MaxAge(Cell(ImportColumn.MtaStsMaxAge), problems),
            problems);
    }

    /// <summary>A header compared ignoring case, spaces, hyphens and underscores, so "MTA-STS Max Age" matches.</summary>
    private static string HeaderKey(string header) =>
        new(header.ToLowerInvariant().Where(character => character is not (' ' or '-' or '_')).ToArray());
}
