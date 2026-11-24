using DotMarc.Baselines;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDmarcTargetReader : IDmarcTargetReader
{
    public EffectiveDmarcTarget? Target { get; set; }

    public Task<EffectiveDmarcTarget?> GetAsync(int domainId, CancellationToken cancellationToken) => Task.FromResult(Target);
}
