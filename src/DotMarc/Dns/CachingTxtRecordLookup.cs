using System.Collections.Concurrent;
using DotMarc.DnsPush;

namespace DotMarc.Dns;

/// <summary>Remembers each name's TXT values for its own lifetime, so the SPF editor can recount after every edit
/// without asking DNS again for includes it has already followed.</summary>
public sealed class CachingTxtRecordLookup(ITxtRecordLookup inner) : ITxtRecordLookup
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken)
    {
        if (_values.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var values = await inner.GetTxtValuesAsync(name, cancellationToken).ConfigureAwait(false);
        _values[name] = values;
        return values;
    }

    public Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken) =>
        inner.LookupWithCnameAsync(name, cancellationToken);
}
