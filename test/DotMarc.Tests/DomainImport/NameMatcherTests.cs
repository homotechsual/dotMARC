using DotMarc.DomainImport;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class NameMatcherTests
{
    [Fact]
    public void FindExisting_IgnoresCase()
    {
        Assert.Equal("Client A", NameMatcher.FindExisting("client a", ["Client A", "Client B"]));
        Assert.Null(NameMatcher.FindExisting("Client C", ["Client A"]));
    }

    [Fact]
    public void IsSameName_IgnoresCasePunctuationAndSuffixes_ButNotTypos()
    {
        Assert.True(NameMatcher.IsSameName("contoso ltd.", "Contoso Limited"));
        Assert.False(NameMatcher.IsSameName("Client C", "Client A"));
    }

    [Fact]
    public void ALooseMatch_IsSuggestedFirst()
    {
        Assert.Equal(["Contoso Limited"], NameMatcher.Suggest("contoso", ["Fabrikam", "Contoso Limited", "Contoso Europe"]));
    }

    [Theory]
    [InlineData("Nortwind Traders", "Northwind Traders")]
    [InlineData("Fabrikan", "Fabrikam")]
    [InlineData("Northwnd Tradrs", "Northwind Traders")]
    public void OneOrTwoLetterTypos_AreSuggested(string typed, string expected)
    {
        Assert.Equal([expected], NameMatcher.Suggest(typed, ["Northwind Traders", "Fabrikam"]));
    }

    [Fact]
    public void UnrelatedAndShortNames_AreNotSuggested()
    {
        Assert.Empty(NameMatcher.Suggest("Woodgrove", ["Northwind Traders", "Fabrikam"]));
        Assert.Empty(NameMatcher.Suggest("Ops", ["Dev", "QA"]));
    }

    [Fact]
    public void AtMostThree_BestFirst()
    {
        var suggestions = NameMatcher.Suggest("Client", ["Clients", "Client Ltd", "Clien", "Cliant", "Clxxnt"]);

        Assert.Equal(3, suggestions.Count);
        Assert.Equal("Client Ltd", suggestions[0]);
    }
}
