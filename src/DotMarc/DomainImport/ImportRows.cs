namespace DotMarc.DomainImport;

/// <summary>One row of input, with its line (CSV) or row (Excel) number so the preview can point at it. Cells are
/// trimmed.</summary>
public sealed record ImportRow(int LineNumber, IReadOnlyList<string> Cells);

/// <summary>Refuses the whole input: over a size or row limit, not UTF-8, or an unreadable spreadsheet. The message is
/// shown to the person as it is.</summary>
public sealed class ImportInputException(string message) : Exception(message);

public static class ImportRows
{
    /// <summary>Adds a row unless it's blank or a comment (its first cell starts with #).</summary>
    public static void AddIfData(List<ImportRow> rows, int lineNumber, IEnumerable<string?> rawCells)
    {
        var cells = rawCells.Select(cell => (cell ?? "").Trim()).ToList();
        if (cells.All(cell => cell.Length == 0) || cells[0].StartsWith('#'))
        {
            return;
        }

        rows.Add(new ImportRow(lineNumber, cells));
    }

    /// <summary>Copies a stream into memory, refusing it as soon as it passes <paramref name="maximumBytes"/>.</summary>
    public static async Task<MemoryStream> ReadLimitedAsync(Stream stream, int maximumBytes, string tooLargeMessage, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes)
            {
                throw new ImportInputException(tooLargeMessage);
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
