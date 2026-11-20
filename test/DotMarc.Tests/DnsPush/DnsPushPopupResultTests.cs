using DotMarc.DnsPush;
using DotMarc.Components.Shared;
using DotMarc.Demo;
using DotMarc.DevBuild;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.DnsPush;

/// <summary>The page the push popup ends on. Some providers' sign-in pages (Cloudflare's among them) cut the popup off
/// from the page that opened it, so the page can't rely on reaching it: it says what happened, and tells the opener over
/// a BroadcastChannel as well as postMessage.</summary>
public sealed class DnsPushPopupResultTests
{
    private const string PushId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private static readonly AppTitle Title = new(Options.Create(new DemoOptions()), Options.Create(new DevBuildOptions()));

    [Theory]
    [InlineData("pushed", "Records pushed")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("zone-not-found", "Couldn't find the domain")]
    [InlineData("replace-failed", "no record at that name")]
    [InlineData("spf-duplicate", "two SPF records")]
    [InlineData("error", "push failed")]
    [InlineData("nothing-to-push", "already matches")]
    public void EachOutcome_IsSaidInWords(string outcome, string words)
    {
        var html = System.Net.WebUtility.HtmlDecode(DnsPushPopupResult.Html(outcome, PushId, Title));

        Assert.Contains(words, html);
        Assert.Contains("You can close this window", html);
    }

    [Fact]
    public void AnUnknownOutcome_StillSaysSomething()
    {
        Assert.Contains("The DNS push finished", DnsPushPopupResult.Html("something-new", PushId, Title));
    }

    [Fact]
    public void TheResult_IsBroadcast_AndPostedToTheOpener_WithThePushId()
    {
        var html = DnsPushPopupResult.Html("pushed", PushId, Title);

        Assert.Contains("BroadcastChannel(\"dotmarc-dns-push\")", html);
        Assert.Contains("window.opener", html);
        Assert.Contains($"\"pushId\":\"{PushId}\"", html);
        Assert.Contains("\"dnsPush\":\"pushed\"", html);
    }

    [Fact]
    public void WithoutAPushId_ItStillSaysWhatHappened()
    {
        var html = DnsPushPopupResult.Html("invalid", null, Title);

        Assert.Contains("\"pushId\":null", html);
        Assert.Contains("You can close this window", html);
    }

    [Fact]
    public void AValueCantBreakOutOfThePage()
    {
        var html = DnsPushPopupResult.Html("</script><script>alert(1)</script>", "\"};alert(1);//", Title);

        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.DoesNotContain("\"};alert(1);//\"", html);
    }

    [Theory]
    [InlineData(false, false, "dotMARC - DNS push")]
    [InlineData(true, false, "[Demo] dotMARC - DNS push")]
    [InlineData(false, true, "[Next] dotMARC - DNS push")]
    [InlineData(true, true, "[Next] dotMARC - DNS push")]
    public void PopupTitle_UsesTheSameBuildLabel(bool demo, bool next, string expected)
    {
        var title = new AppTitle(Options.Create(new DemoOptions { Enabled = demo }),
            Options.Create(new DevBuildOptions { Enabled = next }));

        Assert.Contains($"<title>{expected}</title>", DnsPushPopupResult.Html("pushed", PushId, title));
    }
}
