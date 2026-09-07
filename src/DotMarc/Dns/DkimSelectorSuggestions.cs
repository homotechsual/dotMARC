namespace DotMarc.Dns;

/// <summary>A pre-fill suggestion, not an applied value - ConfigureDkimSelectorsDialog uses this to
/// pre-populate its selectors text box for a domain with no selectors configured yet, editable or
/// clearable by the admin before saving. Scoped to the five inbox providers with a reliable,
/// universal MX-pattern-to-selector convention (matching MailServiceDetector's InboxProviders
/// table by provider name) - sending-only services have per-account DKIM setup that can't be
/// usefully pre-filled.</summary>
public static class DkimSelectorSuggestions
{
    private static readonly Dictionary<string, List<string>> SelectorsByProvider = new(StringComparer.Ordinal)
    {
        ["Microsoft 365"] = ["selector1", "selector2"],
        ["Google Workspace"] = ["google"],
        ["Zoho Mail"] = ["zoho1"],
        ["Fastmail"] = ["fm1", "fm2", "fm3"],
        ["ProtonMail"] = ["protonmail2", "protonmail3"]
    };

    public static List<string>? GetSuggestedSelectors(string providerName) =>
        SelectorsByProvider.TryGetValue(providerName, out var selectors) ? selectors : null;
}
