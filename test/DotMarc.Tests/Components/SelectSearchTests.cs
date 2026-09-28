// test/DotMarc.Tests/Components/SelectSearchTests.cs
using DotMarc.Components.Shared;
using Xunit;

namespace DotMarc.Tests.Components;

public sealed class SelectSearchTests
{
    private static readonly SelectOption<int>[] Options =
    [
        new(1, "Acme Holdings"),
        new(2, "McGarvey Immigration & Asylum Practitioners"),
        new(3, "Contoso Ltd")
    ];

    [Fact]
    public void Filter_WithNoSearchText_ReturnsEveryOptionInOrder()
    {
        Assert.Equal([1, 2, 3], SelectSearch.Filter(Options, null).Select(option => option.Value));
        Assert.Equal([1, 2, 3], SelectSearch.Filter(Options, "   ").Select(option => option.Value));
    }

    [Fact]
    public void Filter_MatchesAnywhereInTheText_IgnoringCase()
    {
        Assert.Equal([2], SelectSearch.Filter(Options, "asylum").Select(option => option.Value));
        Assert.Equal([1], SelectSearch.Filter(Options, "HOLD").Select(option => option.Value));
    }

    [Fact]
    public void Filter_IgnoresSpacesAroundTheSearchText()
    {
        Assert.Equal([3], SelectSearch.Filter(Options, "  contoso ").Select(option => option.Value));
    }

    [Fact]
    public void Filter_WithNoMatch_ReturnsNothing()
    {
        Assert.Empty(SelectSearch.Filter(Options, "fabrikam"));
    }
}
