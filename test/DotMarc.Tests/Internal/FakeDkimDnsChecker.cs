using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDkimDnsChecker : IDkimDnsChecker
{
    public DkimCheckResult Result { get; set; } = new(DkimCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public IReadOnlyList<DkimExpectedRecord>? LastExpectedRecords { get; private set; }

    /// <summary>What each selector has published, by selector name; anything else has nothing.</summary>
    public Dictionary<string, DotMarc.DnsPush.DnsRecordLookupResult> Published { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<DotMarc.DnsPush.DnsRecordLookupResult> LookupSelectorAsync(string domainName, string selector, CancellationToken cancellationToken) =>
        Task.FromResult(Published.TryGetValue(selector, out var published) ? published : new DotMarc.DnsPush.DnsRecordLookupResult(null, null));

    public Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null)
    {
        LastExpectedRecords = expectedRecords;
        CheckedDomains.Add(domainName);
        if (ShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(Result);
    }
}
