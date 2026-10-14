using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

/// <summary>The page the push popup ends on. Some providers' sign-in pages (Cloudflare's among them) cut the popup off
/// from the page that opened it, so the page can't rely on reaching it: it says what happened, and tells the opener over
/// a BroadcastChannel as well as postMessage.</summary>
public sealed class DnsPushPopupResultTests
{
    private const string PushId = "0f8fad5b-d9cb-469f-a165-70867728950e";

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
        var html = System.Net.WebUtility.HtmlDecode(DnsPushPopupResult.Html(outcome, PushId));

        Assert.Contains(words, html);
        Assert.Contains("You can close this window", html);
    }

    [Fact]
    public void AnUnknownOutcome_StillSaysSomething()
    {
        Assert.Contains("The DNS push finished", DnsPushPopupResult.Html("something-new", PushId));
    }

    [Fact]
    public void TheResult_IsBroadcast_AndPostedToTheOpener_WithThePushId()
    {
        var html = DnsPushPopupResult.Html("pushed", PushId);

        Assert.Contains("BroadcastChannel(\"dotmarc-dns-push\")", html);
        Assert.Contains("window.opener", html);
        Assert.Contains($"\"pushId\":\"{PushId}\"", html);
        Assert.Contains("\"dnsPush\":\"pushed\"", html);
    }

    [Fact]
    public void WithoutAPushId_ItStillSaysWhatHappened()
    {
        var html = DnsPushPopupResult.Html("invalid", null);

        Assert.Contains("\"pushId\":null", html);
        Assert.Contains("You can close this window", html);
    }

    [Fact]
    public void AValueCantBreakOutOfThePage()
    {
        var html = DnsPushPopupResult.Html("</script><script>alert(1)</script>", "\"};alert(1);//");

        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.DoesNotContain("\"};alert(1);//\"", html);
    }
}
