namespace DotMarc.DnsPush;

public enum DnsPushOutcome { Pushed, ZoneNotFound, ProviderError }

public sealed record DnsPushResult(DnsPushOutcome Outcome, string? DetailMessage);

/// <summary>One implementation per supported DNS provider. The provider's own OAuth client
/// credentials are DB-backed (CloudflareDnsSettings/AzureDnsSettings), read fresh per call rather
/// than cached — IsConfiguredAsync/BuildAuthorizationUrlAsync need a DB round trip, which is why
/// both are async even though they're conceptually simple lookups. Everything about the end-user's
/// own OAuth exchange stays exactly as stateless as before: nothing about the access token is ever
/// persisted; it exists only as a local variable for the duration of ExchangeAndPushAsync.</summary>
public interface IDnsPushProvider
{
    /// <summary>Matches DetectedDnsProvider and the {provider} route segment in
    /// /dns-push/{provider}/start|callback — "cloudflare" or "azure-dns".</summary>
    string ProviderKey { get; }

    /// <summary>False when this provider's OAuth app isn't configured for this deployment — a
    /// push attempt against this provider then fails with a "no configured option" message rather
    /// than being attempted.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default);

    Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken);
}
