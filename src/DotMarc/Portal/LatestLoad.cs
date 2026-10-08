namespace DotMarc.Portal;

/// <summary>Runs loads that may overlap (one per navigation, say) and reports whether each is still the newest when it
/// finishes, so an older load that finishes last doesn't overwrite a newer one's result.</summary>
public sealed class LatestLoad
{
    private int _generation;

    public async Task<(bool IsLatest, T Value)> RunAsync<T>(Func<Task<T>> load)
    {
        var generation = Interlocked.Increment(ref _generation);
        var value = await load().ConfigureAwait(true);
        return (generation == Volatile.Read(ref _generation), value);
    }
}
