using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfLookupCounterTests
{
    private readonly FakeTxtRecordLookup _lookup = new();

    private Task<SpfLookupCount> CountAsync(string record) =>
        new SpfLookupCounter(_lookup).CountAsync("contoso.com", SpfRecord.Parse(record), CancellationToken.None);

    [Fact]
    public async Task EachLookupTermCostsOne_AndIpAndAllCostNothing()
    {
        var count = await CountAsync("v=spf1 a mx ptr exists:x.example ip4:192.0.2.1 ip6:2001:db8::1 ~all");

        Assert.Equal(4, count.Total);
        Assert.Empty(count.Problems);
    }

    [Fact]
    public async Task Includes_AreFollowed_AndTheirCostAdded()
    {
        _lookup.TxtByName["a.example"] = ["v=spf1 include:b.example ip4:192.0.2.1 ~all"];
        _lookup.TxtByName["b.example"] = ["v=spf1 a mx ~all"];

        var count = await CountAsync("v=spf1 include:a.example mx ~all");

        Assert.Equal(5, count.Total);
        Assert.Equal([new SpfTermCost("include:a.example", 4), new SpfTermCost("mx", 1)], count.TermCosts);
    }

    [Fact]
    public async Task Redirect_IsFollowedToo()
    {
        _lookup.TxtByName["_spf.example.com"] = ["v=spf1 a mx -all"];

        Assert.Equal(3, (await CountAsync("v=spf1 redirect=_spf.example.com")).Total);
    }

    [Fact]
    public async Task AnIncludeWithNoSpfRecord_IsReportedAsMissing()
    {
        _lookup.TxtByName["gone.example"] = ["some other text"];

        var count = await CountAsync("v=spf1 include:gone.example ~all");

        Assert.Equal(1, count.Total);
        Assert.Equal(["gone.example"], count.MissingTargets);
        Assert.Contains(count.Problems, problem => problem.Contains("gone.example has no SPF record"));
    }

    [Fact]
    public async Task MoreThanTwoLookupsThatFindNothing_IsAProblem()
    {
        var count = await CountAsync("v=spf1 include:a.example include:b.example include:c.example ~all");

        Assert.Contains(count.Problems, problem => problem.Contains("3 lookups found nothing"));
    }

    [Fact]
    public async Task ALoop_IsReported_AndCountingStillFinishes()
    {
        _lookup.TxtByName["a.example"] = ["v=spf1 include:b.example ~all"];
        _lookup.TxtByName["b.example"] = ["v=spf1 include:a.example ~all"];

        var count = await CountAsync("v=spf1 include:a.example ~all");

        Assert.Equal(3, count.Total);
        Assert.Contains(count.Problems, problem => problem.Contains("loops"));
    }

    [Fact]
    public async Task TheSameIncludeTwiceInDifferentBranches_CountsTwice_AndIsNotALoop()
    {
        _lookup.TxtByName["a.example"] = ["v=spf1 include:shared.example ~all"];
        _lookup.TxtByName["b.example"] = ["v=spf1 include:shared.example ~all"];
        _lookup.TxtByName["shared.example"] = ["v=spf1 a ~all"];

        var count = await CountAsync("v=spf1 include:a.example include:b.example ~all");

        Assert.Equal(6, count.Total);
        Assert.Empty(count.Problems);
    }

    [Fact]
    public async Task NestingDeeperThanTenLevels_StopsWithAProblem()
    {
        for (var level = 1; level <= 12; level++)
        {
            _lookup.TxtByName[$"level{level}.example"] = [$"v=spf1 include:level{level + 1}.example ~all"];
        }

        var count = await CountAsync("v=spf1 include:level1.example ~all");

        Assert.Contains(count.Problems, problem => problem.Contains("more than 10 levels"));
        Assert.True(count.IsOverLimit);
    }

    [Fact]
    public async Task CountingStopsOncePastTwenty()
    {
        var count = await CountAsync("v=spf1 " + string.Join(' ', Enumerable.Range(1, 30).Select(number => $"exists:{number}.example")) + " ~all");

        Assert.Equal(21, count.Total);
        Assert.True(count.IsOverLimit);
    }

    [Fact]
    public async Task AMacro_IsCountedButNotFollowed()
    {
        var count = await CountAsync("v=spf1 include:%{d}.example ~all");

        Assert.Equal(1, count.Total);
        Assert.Contains(count.Problems, problem => problem.Contains("macro"));
        Assert.DoesNotContain("%{d}.example", _lookup.Queried);
    }
}
