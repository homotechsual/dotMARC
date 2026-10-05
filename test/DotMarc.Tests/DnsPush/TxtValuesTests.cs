using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class TxtValuesTests
{
    [Fact]
    public void Split_CutsLongValuesInto255CharacterStrings()
    {
        var value = new string('a', 300);

        Assert.Equal([255, 45], TxtValues.Split(value).Select(chunk => chunk.Length));
        Assert.Equal(["short"], TxtValues.Split("short"));
    }

    [Fact]
    public void ToQuotedText_QuotesEachStringAndEscapes()
    {
        Assert.Equal("\"v=spf1 -all\"", TxtValues.ToQuotedText("v=spf1 -all"));
        Assert.Equal("\"say \\\"hi\\\"\"", TxtValues.ToQuotedText("say \"hi\""));
        Assert.Equal(2, TxtValues.ToQuotedText(new string('a', 300)).Split("\" \"").Length);
    }

    [Theory]
    [InlineData("\"v=spf1 \" \"-all\"", "v=spf1 -all")]
    [InlineData("\"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("bare value", "bare value")]
    public void FromQuotedText_ReadsBackTheValue(string text, string expected)
    {
        Assert.Equal(expected, TxtValues.FromQuotedText(text));
    }

    [Fact]
    public void ReplaceValues_KeepsEveryOtherValue()
    {
        var (values, missing) = TxtValues.ReplaceValues(
            ["google-site-verification=abc", "v=spf1 include:old.example ~all", "MS=ms123"],
            ["v=spf1 include:old.example ~all"],
            "v=spf1 include:new.example ~all");

        Assert.Equal(["google-site-verification=abc", "MS=ms123", "v=spf1 include:new.example ~all"], values);
        Assert.Empty(missing);
    }

    [Fact]
    public void ReplaceValues_ReportsValuesNoLongerThere()
    {
        var (_, missing) = TxtValues.ReplaceValues(["v=spf1 -all"], ["v=spf1 include:old.example ~all"], "v=spf1 ~all");

        Assert.Equal(["v=spf1 include:old.example ~all"], missing);
    }

    [Fact]
    public void ReplaceValues_DoesntAddAValueTwice()
    {
        var (values, _) = TxtValues.ReplaceValues(["v=spf1 a ~all", "v=spf1 mx ~all"], ["v=spf1 mx ~all"], "v=spf1 a ~all");

        Assert.Equal(["v=spf1 a ~all"], values);
    }
}
