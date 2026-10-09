namespace DotMarc.Notifications;

/// <summary>Which webhook URLs dotMARC will post alerts to: absolute HTTPS, with no credentials in the URL. Shared by
/// saving the settings and by Send test, so a test never posts anywhere a saved setting couldn't.</summary>
public static class WebhookUrls
{
    /// <summary>Why the URL can't be used, or null if it can. A blank one is allowed: that channel just isn't set up.</summary>
    public static string? Problem(string? value, string settingName) =>
        string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo))
            ? null
            : $"{settingName} must be an absolute HTTPS URL without embedded credentials.";
}
