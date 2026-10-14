using DotMarc.Ingestion;
using Xunit;

namespace DotMarc.Tests.Ingestion;

public sealed class PollNowSignalTests
{
    [Fact]
    public async Task ARequest_EndsTheWaitBetweenPolls()
    {
        var signal = new PollNowSignal();
        var waiting = signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        _ = signal.RequestAsync();

        Assert.Same(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task ARequest_CompletesOnceThePollItWasTakenForHasRun()
    {
        var signal = new PollNowSignal();
        var request = signal.RequestAsync();

        var taken = signal.TakeRequests();
        Assert.False(request.IsCompleted);

        PollNowSignal.Complete(taken);
        await request.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARequestMadeDuringAPoll_StartsTheNextOneStraightAway()
    {
        var signal = new PollNowSignal();
        _ = signal.TakeRequests();
        _ = signal.RequestAsync();

        var waiting = signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Same(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task WithoutARequest_TheWaitLastsTheInterval()
    {
        var signal = new PollNowSignal();
        _ = signal.RequestAsync();
        await signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        var waiting = signal.WaitAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(100))));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
