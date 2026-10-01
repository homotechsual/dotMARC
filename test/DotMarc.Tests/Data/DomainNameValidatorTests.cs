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

    [Theory]
    [InlineData("sub.domain.co.uk", "sub.domain.co.uk")]
    [InlineData("Contoso.com.", "contoso.com")]
    [InlineData("a-b.example", "a-b.example")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]
    public void TryNormalize_AcceptsRealHostnames(string input, string expected)
    {
        Assert.True(DomainNameValidator.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("https://contoso.com/", "web address")]
    [InlineData("http://contoso.com", "web address")]
    [InlineData("contoso.com/", "web address")]
    [InlineData("www.contoso.com/about", "web address")]
    [InlineData("sam@contoso.com", "email address")]
    public void TryNormalize_ExplainsWhyUrlsAndEmailAddressesAreRefused(string input, string reasonMentions)
    {
        Assert.False(DomainNameValidator.TryNormalize(input, out _, out var reason));
        Assert.Contains(reasonMentions, reason);
    }

    [Theory]
    [InlineData("192.168.0.1")]
    [InlineData("-bad.com")]
    [InlineData("bad-.com")]
    [InlineData("contoso..com")]
    [InlineData("under_score.com")]
    [InlineData(".com")]
    public void TryNormalize_RefusesNamesThatArentHostnames(string input)
    {
        Assert.False(DomainNameValidator.TryNormalize(input, out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryNormalize_RefusesALabelOver63Characters_AndANameOver253()
    {
        Assert.False(DomainNameValidator.TryNormalize(new string('a', 64) + ".com", out _));
        Assert.True(DomainNameValidator.TryNormalize(new string('a', 63) + ".com", out _));

        var tooLong = string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + ".com";
        Assert.False(DomainNameValidator.TryNormalize(tooLong, out _));
    }
}
