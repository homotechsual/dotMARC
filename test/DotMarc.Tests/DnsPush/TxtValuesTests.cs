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

    private static TxtSetEdit<string> Plan(string[] current, string[] remove, string add) =>
        TxtValues.PlanReplace(current, value => value, remove, add);

    [Fact]
    public void PlanReplace_KeepsEveryOtherValueExactlyAsItWas()
    {
        var edit = Plan(["google-site-verification=abc ", "v=spf1 include:old.example ~all", "MS=ms123"], ["v=spf1 include:old.example ~all"], "v=spf1 include:new.example ~all");

        Assert.Null(edit.Problem);
        Assert.Equal(["google-site-verification=abc ", "MS=ms123"], edit.Kept);
        Assert.Equal(["v=spf1 include:old.example ~all"], edit.Removed);
        Assert.True(edit.AddNew);
    }

    [Fact]
    public void PlanReplace_MatchesAValueWithStraySpaces()
    {
        // A trailing space pasted into a DNS console mustn't stop the old SPF record being found and removed.
        var edit = Plan(["v=spf1 include:old.example ~all "], ["v=spf1 include:old.example ~all "], "v=spf1 include:new.example ~all");

        Assert.Null(edit.Problem);
        Assert.Equal(["v=spf1 include:old.example ~all "], edit.Removed);
        Assert.Empty(edit.Kept);
    }

    [Fact]
    public void PlanReplace_RefusesWhenAValueToRemoveIsGone()
    {
        var edit = Plan(["v=spf1 -all"], ["v=spf1 include:old.example ~all"], "v=spf1 ~all");

        Assert.Equal(["v=spf1 include:old.example ~all"], edit.Missing);
        Assert.NotNull(edit.Problem);
    }

    [Fact]
    public void PlanReplace_RefusesToLeaveTwoSpfRecords()
    {
        // The zone has an SPF record the push didn't know about (a stale DNS read), so adding one would make two.
        var edit = Plan(["v=spf1 include:someone-else.example ~all"], [], "v=spf1 include:new.example ~all");

        Assert.NotNull(edit.Problem);
        Assert.Contains("2 SPF records", edit.Problem);
    }

    [Fact]
    public void PlanReplace_DoesntAddAValueTwice()
    {
        var edit = Plan(["v=spf1 a ~all", "v=spf1 mx ~all"], ["v=spf1 mx ~all"], "v=spf1 a ~all");

        Assert.Null(edit.Problem);
        Assert.False(edit.AddNew);
        Assert.Equal(["v=spf1 a ~all"], edit.Kept);
    }
}
