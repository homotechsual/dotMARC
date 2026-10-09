namespace DotMarc.Tests.Internal;

/// <summary>A clock stopped at a chosen moment, which a test can move forward.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
{
    private DateTimeOffset _nowUtc = nowUtc;

    public override DateTimeOffset GetUtcNow() => _nowUtc;

    public void Advance(TimeSpan by) => _nowUtc += by;
}
