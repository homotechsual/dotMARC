using DotMarc.DnsPush;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDmarcAuthorizationTxtLookup : IDmarcAuthorizationTxtLookup
{
    public DnsRecordLookupResult Result { get; set; } = new(null, null);
    public List<string> LookedUp { get; } = [];

    public Task<DnsRecordLookupResult> LookupAsync(string recordName, CancellationToken cancellationToken)
    {
        LookedUp.Add(recordName);
        return Task.FromResult(Result);
    }
}
