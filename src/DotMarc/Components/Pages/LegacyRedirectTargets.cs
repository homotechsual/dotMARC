namespace DotMarc.Components.Pages;

/// <summary>Old addresses and the page that replaced each, for <see cref="LegacyRedirects"/>.</summary>
public static class LegacyRedirectTargets
{
    private static readonly Dictionary<string, string> Destinations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/senders/baseline"] = "/baselines/sender",
        ["/psa/settings"] = "/alerts/psa",
    };

    /// <summary>Where an old address now lives, with its query string kept, or null when the address is not an old one.</summary>
    public static string? DestinationFor(Uri uri) =>
        Destinations.TryGetValue(uri.AbsolutePath.TrimEnd('/'), out var destination) ? destination + uri.Query : null;
}
