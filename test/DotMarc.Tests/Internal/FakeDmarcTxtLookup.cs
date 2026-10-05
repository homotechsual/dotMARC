using DotMarc.DnsPush;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDmarcTxtLookup : IDmarcTxtLookup
{
    public DnsRecordLookupResult Result { get; set; } = new(null, null);

    public Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken) => Task.FromResult(Result);
}
