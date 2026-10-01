using DotMarc.DomainImport;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class ImportTableTests
{
    private static ImportTable Table(string csv) => ImportTable.FromRows(CsvImportReader.Read(csv));

    [Fact]
    public void APlainList_IsDomainsOnly()
    {
        var table = Table("contoso.com\nfabrikam.com");

        Assert.Equal([ImportColumn.Domain], table.Columns);
        Assert.Equal(["contoso.com", "fabrikam.com"], table.Rows.Select(row => row.RawDomain));
        Assert.All(table.Rows, row => Assert.Null(row.Groups));
    }

    [Fact]
    public void WithoutAHeader_ColumnsAreReadInOrder()
    {
        var row = Table("contoso.com,Client A,primary,Contoso Ltd,no").Rows.Single();

        Assert.Equal(["Client A"], row.Groups!.Names);
        Assert.Equal(["primary"], row.Tags!.Names);
        Assert.Equal("Contoso Ltd", row.HaloClient);
        Assert.False(row.Monitored);
    }

    [Fact]
    public void AHeader_MapsColumnsByNameInAnyOrder_AndWarnsAboutUnknownOnes()
    {
        var table = Table("Monitored,Colour,Domain,MTA_STS Max-Age\nyes,red,contoso.com,86400");

        Assert.Equal(new HashSet<ImportColumn> { ImportColumn.Monitored, ImportColumn.Domain, ImportColumn.MtaStsMaxAge }, table.Columns);
        var row = table.Rows.Single();
        Assert.Equal(("contoso.com", true, 86400), (row.RawDomain, row.Monitored, row.MtaStsMaxAgeSeconds));
        Assert.Contains(table.Warnings, warning => warning.Contains("Colour"));
    }

    [Fact]
    public void NameLists_TrimEntries_DropEmptyOnes_AndSplitOutRemovals()
    {
        var groups = Table("contoso.com,Client A; ;-Old Client ; Client B").Rows.Single().Groups!;

        Assert.Equal(["Client A", "Client B"], groups.Names);
        Assert.Equal(["Old Client"], groups.Removals);
    }

    [Fact]
    public void BlankCells_AreNull()
    {
        var row = Table("domain,groups,monitored,mta-sts mode\ncontoso.com,,,").Rows.Single();

        Assert.Null(row.Groups);
        Assert.Null(row.Monitored);
        Assert.Null(row.MtaStsMode);
        Assert.Empty(row.Problems);
    }

    [Fact]
    public void ValuesThatDontMakeSense_AreLeftOut_WithTheReason()
    {
        var row = Table("domain,monitored,mta-sts mode,mta-sts max age,dkim selectors,mta-sts mx hosts\ncontoso.com,maybe,sometimes,0,bad selector!,not a host").Rows.Single();

        Assert.Null(row.Monitored);
        Assert.Null(row.MtaStsMode);
        Assert.Null(row.MtaStsMaxAgeSeconds);
        Assert.Null(row.DkimSelectors);
        Assert.Null(row.MtaStsMxHosts);
        Assert.Equal(5, row.Problems.Count);
        Assert.Contains(row.Problems, problem => problem.Contains("maybe"));
    }

    [Theory]
    [InlineData("YES", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("No", false)]
    public void Monitored_AcceptsYesNoTrueFalseAndOneZero(string cell, bool expected)
    {
        Assert.Equal(expected, Table($"domain,monitored\ncontoso.com,{cell}").Rows.Single().Monitored);
    }

    [Fact]
    public void MtaStsValues_AreParsed()
    {
        var row = Table("domain,mta-sts mode,mx hosts,max age\ncontoso.com,Enforce,mail.contoso.com;*.mx.contoso.com,31557600").Rows.Single();

        Assert.Equal(MtaStsImportMode.Enforce, row.MtaStsMode);
        Assert.Equal(["mail.contoso.com", "*.mx.contoso.com"], row.MtaStsMxHosts);
        Assert.Equal(31_557_600, row.MtaStsMaxAgeSeconds);
    }

    [Fact]
    public void MoreThanAThousandRows_IsRefused()
    {
        var csv = string.Join('\n', Enumerable.Range(1, ImportTable.MaximumDataRows + 1).Select(number => $"d{number}.com"));

        Assert.Throws<ImportInputException>(() => Table(csv));
    }

    [Fact]
    public void NothingToImport_IsRefused()
    {
        Assert.Throws<ImportInputException>(() => Table("# only a comment\n\n"));
        Assert.Throws<ImportInputException>(() => Table("domain,groups"));
    }
}
