namespace DotMarc.DnsPush;

/// <summary>ReplaceFailedAfterDelete is distinct from ProviderError because it means something
/// materially worse: for every other outcome, nothing changed if it wasn't Pushed, but this one
/// means the old record was already deleted before the new one failed to create - the name now
/// has NO record at all, which is worse than the state before the push was attempted. It gets its
/// own outcome specifically so the UI can't collapse it into the same generic "try again" message
/// every other failure gets.</summary>
/// LeftDuplicateRecord means an SPF push added the new record but couldn't remove the old one, so the name now has
/// more than one SPF record (which receivers treat as none) until someone removes the old one by hand.
/// LeftDuplicateDmarcRecord is the same for a DMARC push: the name now has two DMARC records, which receivers ignore.
public enum DnsPushOutcome { Pushed, ZoneNotFound, ProviderError, ReplaceFailedAfterDelete, LeftDuplicateRecord, LeftDuplicateDmarcRecord }

public sealed record DnsPushResult(DnsPushOutcome Outcome, string? DetailMessage);

/// <summary>What a provider keeps from one signed-in exchange so it can push several times without exchanging again: the access token, or a
/// client built from it. Each provider has its own private implementation. It exists only in memory, is never serialised, stored or logged,
/// and is dropped when the pushes it was made for are finished.</summary>
public interface IDnsPushSession
{
}

/// <summary>A session when the exchange worked, or the failure when it didn't. Exactly one of the two is set.</summary>
public sealed record DnsPushSessionResult(IDnsPushSession? Session, DnsPushResult? Failure);

/// <summary>One implementation per supported DNS provider. The provider's own OAuth client
/// credentials are DB-backed (CloudflareDnsSettings/AzureDnsSettings), read fresh per call rather
/// than cached - IsConfiguredAsync/BuildAuthorizationUrlAsync need a DB round trip, which is why
/// both are async even though they're conceptually simple lookups. Everything about the end-user's
/// own OAuth exchange stays exactly as stateless as before: nothing about the access token is ever
/// persisted; it exists only in memory, in the session returned by ExchangeAsync, for as long as the pushes it was made for take.</summary>
public interface IDnsPushProvider
{
    /// <summary>Matches DetectedDnsProvider and the {provider} route segment in
    /// /dns-push/{provider}/start|callback - "cloudflare" or "azure-dns".</summary>
    string ProviderKey { get; }

    /// <summary>False when this provider's OAuth app isn't configured for this deployment - a
    /// push attempt against this provider then fails with a "no configured option" message rather
    /// than being attempted.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default);

    /// <summary>Exchanges the single-use authorization code for a session. Everything that can be pushed to this provider afterwards rides
    /// this one exchange, because the code can't be used twice. Returns the failure, and no session, when the provider isn't configured or
    /// rejects the code.</summary>
    Task<DnsPushSessionResult> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Pushes the changes in order using an existing session, looking up each change's zone as it goes. Stops at the first change
    /// that doesn't return Pushed and returns that result: a change already pushed before a later one fails is NOT rolled back. Can be
    /// called again with the same session for the next domain's changes. A session that isn't this provider's is refused.</summary>
    Task<DnsPushResult> PushAsync(IDnsPushSession session, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken);
}
