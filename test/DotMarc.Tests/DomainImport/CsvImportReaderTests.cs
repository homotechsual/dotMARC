using System.Text;
using DotMarc.DomainImport;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class CsvImportReaderTests
{
    [Fact]
    public void OneDomainPerLine_IsOneCellPerRow()
    {
        var rows = CsvImportReader.Read("contoso.com\nfabrikam.com");

        Assert.Equal([(1, "contoso.com"), (2, "fabrikam.com")], rows.Select(row => (row.LineNumber, row.Cells.Single())));
    }

    [Fact]
    public void QuotedCells_KeepCommasQuotesAndLineBreaks()
    {
        var rows = CsvImportReader.Read("domain,groups\n\"contoso.com\",\"Client A, Europe;\"\"Quoted\"\" Ltd\"\nfabrikam.com,\"two\nlines\"\nnext.com");

        Assert.Equal(["contoso.com", "Client A, Europe;\"Quoted\" Ltd"], rows[1].Cells);
        Assert.Equal((3, "two\nlines"), (rows[2].LineNumber, rows[2].Cells[1]));
        Assert.Equal(5, rows[3].LineNumber);
    }

    [Fact]
    public void BlankLinesCommentsAndEmptyRows_AreSkipped_AndKeepTheirLineNumbers()
    {
        var rows = CsvImportReader.Read("contoso.com\n\n   \n# a comment\n , , \nfabrikam.com");

        Assert.Equal([(1, "contoso.com"), (6, "fabrikam.com")], rows.Select(row => (row.LineNumber, row.Cells[0])));
    }

    [Fact]
    public void ExcelStyleCsv_ReadsLikeACleanFile()
    {
        var excelStyle = CsvImportReader.Read("﻿domain , groups \r\n contoso.com , Client A \r\nfabrikam.com,\r\n");
        var clean = CsvImportReader.Read("domain,groups\ncontoso.com,Client A\nfabrikam.com,");

        Assert.Equal(clean.Select(row => string.Join('|', row.Cells)), excelStyle.Select(row => string.Join('|', row.Cells)));
    }

    [Fact]
    public void OldMacLineEndings_EndARow()
    {
        var rows = CsvImportReader.Read("a.com\rb.com\r\nc.com");

        Assert.Equal(["a.com", "b.com", "c.com"], rows.Select(row => row.Cells[0]));
    }

    [Fact]
    public async Task ReadAsync_RefusesAFileOverOneMegabyte()
    {
        var tooBig = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', CsvImportReader.MaximumBytes + 1)));

        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => CsvImportReader.ReadAsync(tooBig, CancellationToken.None));

        Assert.Contains("1 MB", refusal.Message);
    }

    [Fact]
    public void CellsCopiedFromASpreadsheet_AreSplitOnTabs()
    {
        // Copying cells from Excel and pasting them gives tab-separated lines.
        var rows = CsvImportReader.Read("domain\tgroups\ncontoso.com\tClient A; Client B\nfabrikam.com");

        Assert.Equal(["domain", "groups"], rows[0].Cells);
        Assert.Equal(["contoso.com", "Client A; Client B"], rows[1].Cells);
        Assert.Equal(["fabrikam.com"], rows[2].Cells);
    }

    [Fact]
    public void TabsInsideACommaSeparatedFile_StayInTheCell()
    {
        var rows = CsvImportReader.Read("domain,groups\ncontoso.com,Client\tA");

        Assert.Equal(["contoso.com", "Client\tA"], rows[1].Cells);
    }

    [Fact]
    public async Task ReadAsync_RefusesTextThatIsntUtf8()
    {
        var utf16 = new MemoryStream(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("contoso.com")).ToArray());

        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => CsvImportReader.ReadAsync(utf16, CancellationToken.None));

        Assert.Contains("UTF-8", refusal.Message);
    }
}
