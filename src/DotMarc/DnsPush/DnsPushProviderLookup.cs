namespace DotMarc.DnsPush;

public static class DnsPushProviderLookup
{
    /// <summary>Returns the provider matching providerKey if (and only if) it's actually
    /// configured for this deployment — null for an unknown key, a null key, or a matching but
    /// unconfigured provider (the caller then reports it as unavailable rather than attempting
    /// the redirect).</summary>
    public static async Task<IDnsPushProvider?> FindConfiguredAsync(this IEnumerable<IDnsPushProvider> providers, string? providerKey, CancellationToken cancellationToken = default)
    {
        if (providerKey is null)
        {
            return null;
        }

        foreach (var candidate in providers)
        {
            if (candidate.ProviderKey == providerKey && await candidate.IsConfiguredAsync(cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }
}
