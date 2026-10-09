using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class LatestLoadTests
{
    [Fact]
    public async Task ALoadOvertakenByANewerOne_IsReportedAsStale()
    {
        var latest = new LatestLoad();
        var slowLoad = new TaskCompletionSource<string>();

        var first = latest.RunAsync(() => slowLoad.Task);
        var second = latest.RunAsync(() => Task.FromResult("second"));
        slowLoad.SetResult("first");

        Assert.Equal((true, "second"), await second);
        Assert.False((await first).IsLatest);
    }

    [Fact]
    public async Task ALoneLoad_IsTheLatest()
    {
        var latest = new LatestLoad();

        Assert.Equal((true, 42), await latest.RunAsync(() => Task.FromResult(42)));
    }
}
