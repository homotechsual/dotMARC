using System.Text.RegularExpressions;

namespace DotMarc.ServerLogs;

/// <summary>Masks the kinds of secret that can end up inside log text (a webhook secret in a request
/// path, an OAuth token or client secret in a query string or form, a bearer token) before the text
/// is kept in memory or shown on the Server logs page. It is a safety net for the known shapes, not
/// a guarantee: nothing should log secrets in the first place.</summary>
public static partial class LogRedactor
{
    private const string Mask = "[redacted]";

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = WebhookSecretInPath().Replace(text, "$1" + Mask);
        text = SecretInQueryOrForm().Replace(text, "$1=" + Mask);
        text = BearerToken().Replace(text, "Bearer " + Mask);
        return text;
    }

    [GeneratedRegex(@"(/integrations/halopsa/webhook/)[^\s/?#""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WebhookSecretInPath();

    [GeneratedRegex(@"\b(client_secret|access_token|refresh_token|id_token|token|api[_-]?key|password|secret|code_verifier|code)=[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretInQueryOrForm();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();
}
