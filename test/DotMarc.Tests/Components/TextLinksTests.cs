using DotMarc.Components.Shared;
using Xunit;

namespace DotMarc.Tests.Components;

public sealed class TextLinksTests
{
    [Fact]
    public void ALinkInAMessage_IsSplitOut_WithoutTheFullStopAfterIt()
    {
        var parts = TextLinks.Split("Grant admin consent: https://login.microsoftonline.com/t/adminconsent?client_id=c. Then wait.");

        Assert.Equal(
            [new TextPart("Grant admin consent: ", false), new TextPart("https://login.microsoftonline.com/t/adminconsent?client_id=c", true), new TextPart(". Then wait.", false)],
            parts);
    }

    [Fact]
    public void TextWithoutALink_IsOnePlainPart()
    {
        Assert.Equal([new TextPart("Response status code does not indicate success: 503.", false)], TextLinks.Split("Response status code does not indicate success: 503."));
    }

    [Fact]
    public void OnlyHttpsLinksAreLinked()
    {
        Assert.All(TextLinks.Split("see http://example.com or javascript:alert(1)"), part => Assert.False(part.IsLink));
    }
}
