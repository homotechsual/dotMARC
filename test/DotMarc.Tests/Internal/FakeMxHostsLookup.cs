using DotMarc.MtaSts;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMxHostsLookup : IMxHostsLookup
{
    public Dictionary<string, List<string>> HostsByDomain { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> LookedUp { get; } = [];

    public Task<List<string>> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        lock (LookedUp)
        {
            LookedUp.Add(domainName);
        }

        return Task.FromResult(HostsByDomain.TryGetValue(domainName, out var hosts) ? hosts : []);
    }
}
