using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace DotMarc.DomainImport;

/// <summary>Writes a minimal one-sheet .xlsx: enough for the import sample, and for tests to build spreadsheets
/// without a writing library. Up to 26 columns.</summary>
public static class SimpleXlsxWriter
{
    /// <summary>A formula cell, with the value Excel would have saved for it.</summary>
    public sealed record Formula(string Expression, double SavedValue);

    public static byte[] Build(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Write(archive, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Write(archive, "xl/workbook.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Domains" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Write(archive, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Write(archive, "xl/worksheets/sheet1.xml", SheetXml(rows));
        }

        return output.ToArray();
    }

    private static string SheetXml(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sheet = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            sheet.Append(CultureInfo.InvariantCulture, $"""<row r="{rowNumber}">""");
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Count; columnIndex++)
            {
                var reference = $"{(char)('A' + columnIndex)}{rowNumber}";
                sheet.Append(rows[rowIndex][columnIndex] switch
                {
                    null => "",
                    string text => $"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{SecurityElement.Escape(text)}</t></is></c>""",
                    double number => $"""<c r="{reference}"><v>{number.ToString(CultureInfo.InvariantCulture)}</v></c>""",
                    Formula formula => $"""<c r="{reference}"><f>{SecurityElement.Escape(formula.Expression)}</f><v>{formula.SavedValue.ToString(CultureInfo.InvariantCulture)}</v></c>""",
                    var other => throw new ArgumentException($"A cell can't hold a {other.GetType().Name}.", nameof(rows))
                });
            }

            sheet.Append("</row>");
        }

        return sheet.Append("</sheetData></worksheet>").ToString();
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
