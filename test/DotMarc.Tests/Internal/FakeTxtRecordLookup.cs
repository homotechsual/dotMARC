using DotMarc.Dns;
using DotMarc.DnsPush;

namespace DotMarc.Tests.Internal;

internal sealed class FakeTxtRecordLookup : ITxtRecordLookup
{
    public Dictionary<string, List<string>> TxtByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DnsRecordLookupResult> LookupsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Queried { get; } = [];
    public Exception? ThrowOnQuery { get; set; }

    public Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken)
    {
        Queried.Add(name);
        if (ThrowOnQuery is not null)
        {
            throw ThrowOnQuery;
        }

        return Task.FromResult<IReadOnlyList<string>>(TxtByName.TryGetValue(name, out var values) ? values : []);
    }

    public Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken)
    {
        Queried.Add(name);
        if (ThrowOnQuery is not null)
        {
            throw ThrowOnQuery;
        }

        return Task.FromResult(LookupsByName.TryGetValue(name, out var result) ? result : new DnsRecordLookupResult(null, null));
    }
}
