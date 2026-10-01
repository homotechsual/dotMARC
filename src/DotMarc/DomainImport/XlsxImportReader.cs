using System.Globalization;
using ExcelDataReader;

namespace DotMarc.DomainImport;

/// <summary>Reads the first worksheet of an .xlsx file into rows. Each cell becomes text: numbers and dates in invariant
/// culture, formulas as their saved value.</summary>
public static class XlsxImportReader
{
    public const int MaximumBytes = 5 * 1_048_576;

    // ExcelDataReader looks up the Windows-1252 code page even for .xlsx, and .NET only ships it once this provider is
    // registered. Registering more than once is harmless.
    static XlsxImportReader() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static async Task<IReadOnlyList<ImportRow>> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        if (fileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportInputException("Old Excel .xls files can't be read. Save it as .xlsx or CSV and try again.");
        }

        using var buffer = await ImportRows.ReadLimitedAsync(stream, MaximumBytes,
            "The file is larger than 5 MB. Split it into smaller files.", cancellationToken).ConfigureAwait(false);

        try
        {
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(buffer);
            var rows = new List<ImportRow>();
            var rowNumber = 0;
            while (reader.Read())
            {
                rowNumber++;
                var cells = new List<string>(reader.FieldCount);
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    cells.Add(CellText(reader.GetValue(column)));
                }

                ImportRows.AddIfData(rows, rowNumber, cells);

                // A small, highly compressed file can hold millions of rows, so stop once it's past what an import
                // allows (a header row and the most data rows) rather than reading it all.
                if (rows.Count > ImportTable.MaximumDataRows + 1)
                {
                    throw new ImportInputException($"The spreadsheet has more than {ImportTable.MaximumDataRows:N0} rows, the most an import can have. Split it into smaller imports.");
                }
            }

            return rows;
        }
        catch (Exception exception) when (exception is not ImportInputException and not OperationCanceledException)
        {
            throw new ImportInputException("The spreadsheet couldn't be read. Check it's an .xlsx file, or save it as CSV and try again.");
        }
    }

    private static string CellText(object? value) => value switch
    {
        null => "",
        string text => text,
        double number => number.ToString("0.###############", CultureInfo.InvariantCulture),
        DateTime date when date.TimeOfDay == TimeSpan.Zero => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool flag => flag ? "TRUE" : "FALSE",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
