using System.Collections.Concurrent;
using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Graph;
using DotMarc.MtaSts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotMarc.Demo;

public sealed class DemoDnsRecords
{
    private readonly ConcurrentDictionary<(string Type, string Name), string> _values = new();
    public string? Get(string type, string name) => _values.GetValueOrDefault((type, name));
    public void Set(DnsRecordChange change) => _values[(change.RecordType, change.Name)] = change.DesiredValue;
    public void Clear() => _values.Clear();
}

public sealed class DemoDnsPushProvider(string providerKey, DemoDnsRecords records) : IDnsPushProvider
{
    private sealed record Session(string Provider) : IDnsPushSession;
    public string ProviderKey => providerKey;
    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default) =>
        Task.FromResult($"{redirectUri}?code=demo-code&state={Uri.EscapeDataString(state)}");
    public Task<DnsPushSessionResult> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult(code == "demo-code"
            ? new DnsPushSessionResult(new Session(ProviderKey), null)
            : new DnsPushSessionResult(null, new(DnsPushOutcome.ProviderError, "Demo sign-in was not recognized.")));
    public async Task<DnsPushResult> PushAsync(IDnsPushSession session, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)
    {
        if (session is not Session signedIn || signedIn.Provider != ProviderKey)
            return new(DnsPushOutcome.ProviderError, "This is not a session for this demo provider.");
        await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        if (changes.Any(change => change.ZoneName == "cobalt-freight.example" || change.ZoneName.Contains("broken", StringComparison.OrdinalIgnoreCase)))
            return new(DnsPushOutcome.ProviderError, "The demo provider rejected this record. No real DNS was changed.");
        foreach (var change in changes) records.Set(change);
        return new(DnsPushOutcome.Pushed, null);
    }
}

public sealed class DemoDnsProviderDetector(IDbContextFactory<DotMarcDbContext> factory) : IDnsProviderDetector
{
    public async Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.AsNoTracking().SingleOrDefaultAsync(domain => domain.Name == domainName, cancellationToken);
        return new(domain?.DnsProvider ?? DetectedDnsProvider.Unknown, domainName, domain?.DnsNameservers ?? []);
    }
}

public sealed class DemoDmarcTxtLookup(DemoDnsRecords records, IOptions<GraphOptions> graph,
    IDbContextFactory<DotMarcDbContext> factory, IDnsRecordSettingsReader settings, IDmarcTargetReader targets) : IDmarcTxtLookup
{
    public async Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        if (records.Get("TXT", $"_dmarc.{domainName}") is { } stored) return new(stored, null);
        if (domainName != "aurora-retail.example" && !domainName.StartsWith("already-", StringComparison.Ordinal)) return new(null, null);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var id = await db.Domains.Where(domain => domain.Name == domainName).Select(domain => domain.Id).SingleAsync(cancellationToken);
        var target = await targets.GetAsync(id, cancellationToken);
        var address = await settings.GetReportAddressAsync(id, cancellationToken);
        return new($"v=DMARC1; {(target is null ? "p=none" : DmarcTargetText.Policy(target))}; {DmarcRuaMerge.RuaTag(graph.Value.MailboxAddress, address.Address)}", null);
    }
}

public sealed class DemoTlsrptTxtLookup(DemoDnsRecords records, IOptions<GraphOptions> graph) : ITlsrptTxtLookup
{
    public Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken) =>
        Task.FromResult(new DnsRecordLookupResult(records.Get("TXT", $"_smtp._tls.{domainName}")
            ?? (domainName == "aurora-retail.example" || domainName.StartsWith("already-", StringComparison.Ordinal)
                ? $"v=TLSRPTv1; rua=mailto:{graph.Value.TlsrptMailboxAddress}" : null), null));
}

public sealed class DemoMtaStsCnameLookup(DemoDnsRecords records) : IMtaStsCnameLookup
{
    public Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken) =>
        Task.FromResult(records.Get("CNAME", $"mta-sts.{domainName}"));
    public Task<string?> LookupAsuidTxtAsync(string domainName, CancellationToken cancellationToken) =>
        Task.FromResult(records.Get("TXT", $"asuid.mta-sts.{domainName}"));
}
