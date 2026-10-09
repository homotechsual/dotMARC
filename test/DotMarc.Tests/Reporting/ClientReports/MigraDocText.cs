using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace DotMarc.Tests.Reporting.ClientReports;

/// <summary>All the text in a MigraDoc document, for asserting on what a report says without parsing the PDF (whose
/// embedded font stores glyph ids, not letters).</summary>
internal static class MigraDocText
{
    public static string Of(Document document)
    {
        var text = new StringBuilder();
        foreach (Section section in document.Sections)
        {
            Append(section.Elements, text);
            Append(section.Footers.Primary.Elements, text);
        }

        return text.ToString();
    }

    private static void Append(DocumentElements elements, StringBuilder text)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                    AppendInline(paragraph.Elements, text);
                    text.AppendLine();
                    break;
                case Table table:
                    foreach (Row row in table.Rows)
                    {
                        foreach (Cell cell in row.Cells)
                        {
                            Append(cell.Elements, text);
                        }
                    }

                    break;
            }
        }
    }

    private static void AppendInline(ParagraphElements elements, StringBuilder text)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case Text run: text.Append(run.Content); break;
                case FormattedText formatted: AppendInline(formatted.Elements, text); break;
                case Character character when character.SymbolName == SymbolName.Blank: text.Append(' '); break;
            }
        }
    }
}
