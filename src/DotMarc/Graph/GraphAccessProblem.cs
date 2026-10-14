using System.Net;
using System.Text;
using System.Text.Json;
using DotMarc.Security;

namespace DotMarc.Graph;

/// <summary>Graph refused the mailbox for a reason someone can fix, with the message saying what to do.</summary>
public sealed class GraphAccessException(string message, HttpStatusCode statusCode) : HttpRequestException(message, null, statusCode);

/// <summary>Turns Graph's bare 401, 403 or mailbox 404 into what's actually wrong. An app-only token is issued whether
/// or not the app has been granted any permission, so a tenant that never gave admin consent only shows up as a 403
/// when the mailbox is read. The token's roles claim tells that apart from an Exchange application access policy that
/// leaves the mailbox out. The token is only read, never logged or shown.</summary>
public static class GraphAccessProblem
{
    private const string ReadWrite = "Mail.ReadWrite";

    /// <summary>A message to show instead of the bare status, or null when the failure isn't one of these.</summary>
    public static string? Describe(HttpStatusCode status, string accessToken, string? graphErrorCode, string mailbox, string tenantId, string clientId, bool isWrite)
    {
        if (status == HttpStatusCode.NotFound)
        {
            return graphErrorCode is "ErrorInvalidUser" or "MailboxNotEnabledForRESTAPI"
                ? $"Microsoft Graph has no mailbox {mailbox} ({graphErrorCode}). Check the mailbox address in dotMARC's Graph settings."
                : null;
        }

        if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return null;
        }

        var consent = SignInFailure.AdminConsentUrl(null, tenantId, clientId) is { } url
            ? $" An Entra administrator can grant it here: {url}"
            : "";
        var roles = RolesIn(accessToken);
        var canRead = roles?.Any(role => role is "Mail.Read" or ReadWrite) ?? false;
        var policy = $"Microsoft Graph refused access to the mailbox {mailbox} ({graphErrorCode ?? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)}). "
            + "An Exchange application access policy probably leaves this mailbox out: check the policy's group contains it, and that the mailbox address is right.";

        if (roles is null)
        {
            return $"Microsoft Graph refused access to the mailbox {mailbox}. Either dotMARC's mail app registration has no Mail.ReadWrite "
                + $"application permission with admin consent, or an Exchange application access policy leaves this mailbox out.{consent}";
        }

        if (!canRead)
        {
            return "dotMARC's mail app registration has no Microsoft Graph mail permission, or it hasn't been given admin consent. "
                + $"In Entra, open the app's API permissions, add the {ReadWrite} application permission, and select Grant admin consent.{consent}";
        }

        if (isWrite && !roles!.Contains(ReadWrite))
        {
            return $"dotMARC can read the mailbox {mailbox} but can't mark reports as read: its mail app registration has Mail.Read, "
                + $"and marking messages needs the {ReadWrite} application permission with admin consent.{consent}";
        }

        return policy;
    }

    /// <summary>The roles in an app-only token, or null when the token can't be read.</summary>
    private static IReadOnlyList<string>? RolesIn(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                ? roles.EnumerateArray().Select(role => role.GetString() ?? "").ToList()
                : [];
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Throws a <see cref="GraphAccessException"/> for a failure Describe explains, after dropping the cached
    /// token so a fix (such as newly granted consent) takes effect on the next poll; otherwise the usual
    /// EnsureSuccessStatusCode.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string accessToken, IGraphTokenProvider tokenProvider, string mailbox, GraphOptions options, bool isWrite, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = Describe(response.StatusCode, accessToken, ErrorCodeIn(body), mailbox, options.TenantId, options.ClientId, isWrite);
        if (message is not null)
        {
            tokenProvider.Invalidate();
            throw new GraphAccessException(message, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }

    private static string? ErrorCodeIn(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
