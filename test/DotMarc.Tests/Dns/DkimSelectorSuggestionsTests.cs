using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DkimSelectorSuggestionsTests
{
    [Theory]
    [InlineData("Microsoft 365", new[] { "selector1", "selector2" })]
    [InlineData("Google Workspace", new[] { "google" })]
    [InlineData("Zoho Mail", new[] { "zoho1" })]
    [InlineData("Fastmail", new[] { "fm1", "fm2", "fm3" })]
    [InlineData("ProtonMail", new[] { "protonmail2", "protonmail3" })]
    public void GetSuggestedSelectors_ReturnsProviderSpecificSelectors(string provider, string[] expected)
    {
        var result = DkimSelectorSuggestions.GetSuggestedSelectors(provider);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetSuggestedSelectors_ReturnsNull_ForAnUnrecognizedProvider()
    {
        Assert.Null(DkimSelectorSuggestions.GetSuggestedSelectors("Some Unknown Provider"));
    }
}
