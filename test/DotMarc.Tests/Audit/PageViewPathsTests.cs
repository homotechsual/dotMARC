using DotMarc.Audit;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class PageViewPathsTests
{
    [Theory]
    [InlineData("https://dotmarc.example/domains/contoso.com/sources", "/domains/contoso.com/sources")]
    [InlineData("https://dotmarc.example/dashboard?dnsPush=invalid#top", "/dashboard")]
    [InlineData("https://dotmarc.example/domains/xn--bcher-kva.example?state=abc", "/domains/xn--bcher-kva.example")]
    [InlineData("https://dotmarc.example/", "/")]
    public void PathOf_KeepsThePathOnly(string absoluteUri, string expectedPath)
    {
        Assert.Equal(expectedPath, PageViewPaths.PathOf(absoluteUri));
    }

    [Theory]
    [InlineData("/domains/contoso.com", "contoso.com")]
    [InlineData("/domains/contoso.com/sources", "contoso.com")]
    [InlineData("/domains", null)]
    [InlineData("/dashboard", null)]
    public void DomainOf_FindsTheDomainADomainPageIsAbout(string path, string? expectedDomain)
    {
        Assert.Equal(expectedDomain, PageViewPaths.DomainOf(path));
    }
}
