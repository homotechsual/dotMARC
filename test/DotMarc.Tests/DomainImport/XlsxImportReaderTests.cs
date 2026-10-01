using System.Text;
using DotMarc.DomainImport;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class XlsxImportReaderTests
{
    private static Task<IReadOnlyList<ImportRow>> ReadAsync(byte[] bytes, string fileName = "domains.xlsx") =>
        XlsxImportReader.ReadAsync(new MemoryStream(bytes), fileName, CancellationToken.None);

    [Fact]
    public async Task ReadsTextNumbersAndAFormulasSavedValue()
    {
        var workbook = SimpleXlsxWriter.Build(
        [
            ["domain", "mta-sts max age"],
            ["contoso.com", 604800.0],
            ["fabrikam.com", new SimpleXlsxWriter.Formula("86400*2", 172800)],
        ]);

        var rows = await ReadAsync(workbook);

        Assert.Equal(["contoso.com", "604800"], rows[1].Cells);
        Assert.Equal("172800", rows[2].Cells[1]);
        Assert.Equal([1, 2, 3], rows.Select(row => row.LineNumber));
    }

    [Fact]
    public async Task BlankAndCommentRows_AreSkipped()
    {
        var workbook = SimpleXlsxWriter.Build([["contoso.com"], [null], ["# note"], ["fabrikam.com"]]);

        var rows = await ReadAsync(workbook);

        Assert.Equal(["contoso.com", "fabrikam.com"], rows.Select(row => row.Cells[0]));
    }

    [Fact]
    public async Task AnXlsFile_IsRefusedWithAWayForward()
    {
        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => ReadAsync([1, 2, 3], "domains.xls"));

        Assert.Contains(".xlsx or CSV", refusal.Message);
    }

    [Fact]
    public async Task AFileThatIsntASpreadsheet_IsRefused()
    {
        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => ReadAsync(Encoding.UTF8.GetBytes("not a spreadsheet")));

        Assert.Contains("couldn't be read", refusal.Message);
    }

    [Fact]
    public async Task AFileOverFiveMegabytes_IsRefused()
    {
        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => ReadAsync(new byte[XlsxImportReader.MaximumBytes + 1]));

        Assert.Contains("5 MB", refusal.Message);
    }

    [Fact]
    public async Task TheSampleFiles_ReadTheSameRows()
    {
        var fromCsv = CsvImportReader.Read(DomainImportSamples.Csv);
        var fromXlsx = await ReadAsync(DomainImportSamples.Xlsx());

        Assert.Equal(fromCsv.Select(row => string.Join('|', row.Cells).TrimEnd('|')), fromXlsx.Select(row => string.Join('|', row.Cells).TrimEnd('|')));
    }
}
