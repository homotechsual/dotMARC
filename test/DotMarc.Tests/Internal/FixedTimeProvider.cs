namespace DotMarc.Tests.Internal;

/// <summary>A clock stopped at a chosen moment.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => nowUtc;
}
