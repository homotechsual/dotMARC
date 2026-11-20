using DotMarc.Data;
using DotMarc.DnsPush;

namespace DotMarc.Tests.Internal;

/// <summary>Detects each domain as being on Cloudflare in a zone named after it, unless a test says otherwise.</summary>
internal sealed class ScriptedDnsProviderDetector : IDnsProviderDetector
{
    public Dictionary<string, DnsProviderDetectionResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        if (Failing.Contains(domainName))
        {
            throw new HttpRequestException("dns lookup failed");
        }

        return Task.FromResult(Results.TryGetValue(domainName, out var result)
            ? result
            : new DnsProviderDetectionResult(DetectedDnsProvider.Cloudflare, domainName, ["ns1.example"]));
    }

    public ScriptedDnsProviderDetector MovedTo(string domainName, DetectedDnsProvider provider)
    {
        Results[domainName] = new DnsProviderDetectionResult(provider, domainName, ["ns1.example"]);
        return this;
    }
}

/// <summary>A provider whose exchange and pushes a test scripts: every push succeeds unless the zone has a result in <see cref="ResultsByZone"/>,
/// and every call is recorded so a test can see how many exchanges and pushes there were and what was pushed.</summary>
internal sealed class FakeDnsPushProvider(string providerKey) : IDnsPushProvider
{
    public sealed class Session : IDnsPushSession
    {
    }

    public string ProviderKey => providerKey;
    public int ExchangeCalls { get; private set; }
    public DnsPushResult? ExchangeFailure { get; set; }
    public Dictionary<string, DnsPushResult> ResultsByZone { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Zone, IReadOnlyList<DnsRecordChange> Changes)> Pushes { get; } = [];
    public Action<string>? BeforePush { get; set; }

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default) =>
        Task.FromResult($"https://{providerKey}.example/authorize?state={Uri.EscapeDataString(state)}");

    public Task<DnsPushSessionResult> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        ExchangeCalls++;
        return Task.FromResult(ExchangeFailure is null ? new DnsPushSessionResult(new Session(), null) : new DnsPushSessionResult(null, ExchangeFailure));
    }

    public Task<DnsPushResult> PushAsync(IDnsPushSession session, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)
    {
        var zone = changes.Count > 0 ? changes[0].ZoneName : "";
        BeforePush?.Invoke(zone);
        cancellationToken.ThrowIfCancellationRequested();
        Pushes.Add((zone, changes));
        return Task.FromResult(ResultsByZone.TryGetValue(zone, out var result) ? result : new DnsPushResult(DnsPushOutcome.Pushed, null));
    }
}

/// <summary>Builds one change per domain whose desired value a test can change between calls, which is how a plan that changed since it was
/// reviewed is simulated. A domain in <see cref="NothingToChange"/> already has the value, and one in <see cref="Refusals"/> is refused.</summary>
internal sealed class ScriptedDnsChangeBuilder(string target, bool writesToDomainZone = true) : IDnsChangeBuilder
{
    public string Target => target;
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => writesToDomainZone;

    public string DefaultValue { get; set; } = "v=DMARC1; p=none";
    public Dictionary<string, string> DesiredValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> NothingToChange { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Refusals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var name = request.Domain.Name;
        if (Refusals.TryGetValue(name, out var refusal))
        {
            return Task.FromResult(DnsChangePlan.Refuse(refusal));
        }

        var desired = DesiredValues.TryGetValue(name, out var value) ? value : DefaultValue;
        var zone = request.DomainZone ?? name;
        return Task.FromResult(DnsChangePlan.Of(NothingToChange.Contains(name)
            ? new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{name}", desired, desired, zone)
            : new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{name}", desired, null, zone)));
    }
}
