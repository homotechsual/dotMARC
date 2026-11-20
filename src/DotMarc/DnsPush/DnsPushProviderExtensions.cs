namespace DotMarc.DnsPush;

public static class DnsPushProviderExtensions
{
    /// <summary>One exchange and one push, for a single domain's changes: the exchange, then (only if it worked) the push against its
    /// session. This is what the single-domain push does, so its behaviour is the same as when each provider did both in one call.</summary>
    public static async Task<DnsPushResult> ExchangeAndPushAsync(
        this IDnsPushProvider provider, string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)
    {
        var exchange = await provider.ExchangeAsync(code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        return exchange.Session is { } session
            ? await provider.PushAsync(session, changes, cancellationToken).ConfigureAwait(false)
            : exchange.Failure ?? new DnsPushResult(DnsPushOutcome.ProviderError, "The sign-in didn't produce a session.");
    }
}
