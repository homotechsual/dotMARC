namespace DotMarc.DnsPush;

/// <summary>ReplaceFailedAfterDelete is distinct from ProviderError because it means something
/// materially worse: for every other outcome, nothing changed if it wasn't Pushed, but this one
/// means the old record was already deleted before the new one failed to create — the name now
/// has NO record at all, which is worse than the state before the push was attempted. It gets its
/// own outcome specifically so the UI can't collapse it into the same generic "try again" message
/// every other failure gets.</summary>
public enum DnsPushOutcome { Pushed, ZoneNotFound, ProviderError, ReplaceFailedAfterDelete }

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

    /// <summary>Pushes every change in order against one token exchange (the authorization code is
    /// single-use, so all changes for one push action have to ride the same exchange). Stops at
    /// the first change that doesn't return Pushed and returns that result — a change already
    /// pushed before a later one fails is NOT rolled back.</summary>
    Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken);
}
