using DotMarc.DnsPush;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsPushStartGuardTests
{
    private static HttpRequest Request(string? fetchSite = null, string? referer = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("dotmarc.example");
        if (fetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        }

        if (referer is not null)
        {
            context.Request.Headers.Referer = referer;
        }

        return context.Request;
    }

    [Theory]
    [InlineData("same-origin", null, true)]
    [InlineData("cross-site", "https://dotmarc.example/domains/contoso.com", false)]
    [InlineData("same-site", null, false)]
    [InlineData("none", null, false)]
    [InlineData(null, "https://dotmarc.example/domains/contoso.com", true)]
    [InlineData(null, "https://attacker.example/page", false)]
    [InlineData(null, null, false)]
    public void OnlyARequestFromDotMarcsOwnPagesStartsAPush(string? fetchSite, string? referer, bool allowed)
    {
        Assert.Equal(allowed, DnsPushStartGuard.IsFromThisSite(Request(fetchSite, referer)));
    }
}
