namespace DotMarc.Ingestion;

/// <summary>Lets someone ask for a mailbox poll now instead of waiting for the next scheduled one. PollingService waits on
/// this between cycles, takes the requests made so far just before polling, and completes them once the mailbox poll has
/// run (whether it worked or not: the poll status says how it went). A request made while a poll is already running
/// starts the next one straight away.</summary>
public sealed class PollNowSignal
{
    private readonly object _gate = new();
    private List<TaskCompletionSource> _requests = [];
    private TaskCompletionSource _requested = NewSource();

    public Task RequestAsync(CancellationToken cancellationToken = default)
    {
        var done = NewSource();
        lock (_gate)
        {
            _requests.Add(done);
            _requested.TrySetResult();
        }

        return done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Waits out the interval, or less if a poll is requested.</summary>
    public async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        Task requested;
        lock (_gate)
        {
            requested = _requested.Task;
        }

        await Task.WhenAny(Task.Delay(interval, cancellationToken), requested).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_requested.Task.IsCompleted)
            {
                _requested = NewSource();
            }
        }
    }

    /// <summary>The requests the coming poll answers. Hand them to <see cref="Complete"/> once it has run.</summary>
    public IReadOnlyList<TaskCompletionSource> TakeRequests()
    {
        lock (_gate)
        {
            var taken = _requests;
            _requests = [];
            return taken;
        }
    }

    public static void Complete(IEnumerable<TaskCompletionSource> requests)
    {
        foreach (var request in requests)
        {
            request.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
