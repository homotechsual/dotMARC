namespace DotMarc.Audit;

/// <summary>Turns a page URL into what a page view records.</summary>
public static class PageViewPaths
{
    /// <summary>The path alone. The query string and fragment are dropped because DNS push state travels in the
    /// query string, and they add nothing to "which page".</summary>
    public static string PathOf(string absoluteUri) => Uri.UnescapeDataString(new Uri(absoluteUri).AbsolutePath);

    /// <summary>The domain a /domains/{name} page (or one of its tabs) is about, or null for any other page,
    /// including the /domains list itself.</summary>
    public static string? DomainOf(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[0] == "domains" ? segments[1] : null;
    }
}
