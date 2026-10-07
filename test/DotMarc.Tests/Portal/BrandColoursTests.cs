using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class BrandColoursTests
{
    [Theory]
    [InlineData("#1A2B3C", true)]
    [InlineData("#abcdef", true)]
    [InlineData("1A2B3C", false)]
    [InlineData("#FFF", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData(null, false)]
    public void OnlySixDigitHexWithAHash_IsValid(string? value, bool expected) => Assert.Equal(expected, BrandColours.IsValid(value));

    [Fact]
    public void BlackOnWhite_Is21To1_AndAColourAgainstItself_Is1To1()
    {
        Assert.Equal(21, BrandColours.ContrastRatio("#000000", "#FFFFFF"), precision: 2);
        Assert.Equal(1, BrandColours.ContrastRatio("#3366CC", "#3366CC"), precision: 2);
    }
}
