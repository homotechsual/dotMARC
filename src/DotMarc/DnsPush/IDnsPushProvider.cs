namespace DotMarc.DnsPush;

public enum DnsPushOutcome { Pushed, ZoneNotFound, ProviderError }

public sealed record DnsPushResult(DnsPushOutcome Outcome, string? DetailMessage);

/// <summary>One implementation per supported DNS provider. Every method is stateless from
/// dotMARC's own perspective — nothing about the OAuth exchange is ever persisted; the access token
/// exists only as a local variable for the duration of ExchangeAndPushAsync.</summary>
public interface IDnsPushProvider
{
    /// <summary>Matches DetectedDnsProvider and the {provider} route segment in
    /// /dns-push/{provider}/start|callback — "cloudflare" or "azure-dns".</summary>
    string ProviderKey { get; }

    /// <summary>False when this provider's OAuth app isn't configured for this deployment — the
    /// push button never renders in that case.</summary>
    bool IsConfigured { get; }

    string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri);

    Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken);
}
