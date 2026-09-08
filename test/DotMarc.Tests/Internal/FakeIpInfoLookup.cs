using DotMarc.Data;
using DotMarc.IpEnrichment;

namespace DotMarc.Tests.Internal;

internal sealed class FakeIpInfoLookup : IIpInfoLookup
{
    public IpLookupResult Result { get; set; } = new(IpLookupStatus.Ok, "Example Org", "US");
    public List<string> LookedUpIps { get; } = [];
    public HashSet<string> IpsToThrowFor { get; } = [];

    public Task<IpLookupResult> LookupAsync(string ip, CancellationToken cancellationToken)
    {
        LookedUpIps.Add(ip);
        if (IpsToThrowFor.Contains(ip))
        {
            throw new HttpRequestException($"Simulated failure for {ip}.");
        }
        return Task.FromResult(Result);
    }
}
