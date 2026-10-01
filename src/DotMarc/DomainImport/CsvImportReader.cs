using System.Text;

namespace DotMarc.DomainImport;

/// <summary>Reads CSV (RFC 4180) and pasted text into rows. A pasted list of domains is just CSV with one column.</summary>
public static class CsvImportReader
{
    public const int MaximumBytes = 1_048_576;

    public static async Task<IReadOnlyList<ImportRow>> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = await ImportRows.ReadLimitedAsync(stream, MaximumBytes,
            "The file is larger than 1 MB. Split it into smaller files.", cancellationToken).ConfigureAwait(false);

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (DecoderFallbackException)
        {
            throw new ImportInputException("The file isn't UTF-8 text. In Excel, save it as \"CSV UTF-8\" and try again.");
        }

        return Read(text);
    }

    public static IReadOnlyList<ImportRow> Read(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var rows = new List<ImportRow>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var lineNumber = 1;
        var rowStartLine = 1;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (character == '\n')
                    {
                        lineNumber++;
                    }

                    cell.Append(character);
                }

                continue;
            }

            switch (character)
            {
                // A quote opens a quoted cell only at the start of a cell (spaces before it are allowed).
                case '"' when cell.ToString().Trim().Length == 0:
                    cell.Clear();
                    inQuotes = true;
                    break;
                case ',':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r' when index + 1 < text.Length && text[index + 1] == '\n':
                    break;
                case '\r':
                case '\n':
                    EndRow();
                    lineNumber++;
                    rowStartLine = lineNumber;
                    break;
                default:
                    cell.Append(character);
                    break;
            }
        }

        EndRow();
        return rows;

        void EndRow()
        {
            cells.Add(cell.ToString());
            cell.Clear();
            ImportRows.AddIfData(rows, rowStartLine, cells);
            cells.Clear();
        }
    }
}
