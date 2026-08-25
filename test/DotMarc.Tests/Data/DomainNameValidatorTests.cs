using DotMarc.Data;
using Xunit;

namespace DotMarc.Tests.Data;

public sealed class DomainNameValidatorTests
{
    [Theory]
    [InlineData("Contoso.com", "contoso.com")]
    [InlineData("  contoso.com  ", "contoso.com")]
    [InlineData("SUB.Contoso.IO", "sub.contoso.io")]
    public void TryNormalize_TrimsAndLowercases_ValidInput(string input, string expected)
    {
        var result = DomainNameValidator.TryNormalize(input, out var normalized);

        Assert.True(result);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodothere")]
    [InlineData("has space.com")]
    [InlineData("contoso .com")]
    public void TryNormalize_RejectsInvalidInput(string input)
    {
        var result = DomainNameValidator.TryNormalize(input, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryNormalize_RejectsNull()
    {
        var result = DomainNameValidator.TryNormalize(null, out var normalized);

        Assert.False(result);
        Assert.Equal("", normalized);
    }
}
