using System.Text.RegularExpressions;

namespace DotMarc.Security;

public enum SignInFailureKind { AdminConsentRequired, Cancelled, Other }

/// <summary>Why an Entra sign-in came back as a failure, worked out from the error Entra sends to the callback. Most
/// often a tenant that only lets administrators approve apps hasn't approved dotMARC yet, which Entra reports as
/// consent_required or AADSTS65001. Without this, the failure reaches the generic error page and says nothing useful.</summary>
public sealed partial record SignInFailure(SignInFailureKind Kind, string? Code)
{
    private const string DefaultInstance = "https://login.microsoftonline.com/";

    // AADSTS65001: no consent yet. AADSTS90094: the grant needs an administrator's approval.
    private static readonly string[] AdminConsentCodes = ["AADSTS65001", "AADSTS90094", "consent_required"];

    // AADSTS65004: the person declined the consent prompt.
    private static readonly string[] CancelledCodes = ["AADSTS65004", "access_denied"];

    /// <summary>Reads the message OpenIdConnectHandler gives a remote failure, such as
    /// "Message contains error: 'consent_required', error_description: 'AADSTS65001: ...'".</summary>
    public static SignInFailure Classify(string? failureMessage)
    {
        var entraCode = EntraCode().Match(failureMessage ?? "") is { Success: true } entraMatch ? entraMatch.Value : null;
        var error = OAuthError().Match(failureMessage ?? "") is { Success: true } errorMatch ? errorMatch.Groups[1].Value : null;
        var code = entraCode ?? error;

        // The Entra code is the more specific, so it decides first: AADSTS90094 arrives as access_denied.
        string[] candidates = [entraCode ?? "", error ?? ""];
        var kind = candidates.Any(AdminConsentCodes.Contains) && !(entraCode is not null && CancelledCodes.Contains(entraCode))
            ? SignInFailureKind.AdminConsentRequired
            : candidates.Any(CancelledCodes.Contains) ? SignInFailureKind.Cancelled : SignInFailureKind.Other;
        return new SignInFailure(kind, code);
    }

    /// <summary>Only a code shaped like an Entra or OAuth error code is shown, since it arrives in the page's query
    /// string where anyone could put anything.</summary>
    public static bool IsCodeLike(string? code) => code is not null && ShownCode().IsMatch(code);

    public string ProblemPagePath
    {
        get
        {
            var reason = Kind switch
            {
                SignInFailureKind.AdminConsentRequired => "admin-consent",
                SignInFailureKind.Cancelled => "cancelled",
                _ => "other"
            };
            return Code is null ? $"/SignInProblem?reason={reason}" : $"/SignInProblem?reason={reason}&code={Uri.EscapeDataString(Code)}";
        }
    }

    /// <summary>The page where an Entra administrator approves dotMARC for the whole tenant, or null when the tenant or
    /// app isn't configured.</summary>
    public static string? AdminConsentUrl(string? instance, string? tenantId, string? clientId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var root = string.IsNullOrWhiteSpace(instance) ? DefaultInstance : instance.TrimEnd('/') + "/";
        return $"{root}{Uri.EscapeDataString(tenantId.Trim())}/adminconsent?client_id={Uri.EscapeDataString(clientId.Trim())}";
    }

    [GeneratedRegex(@"AADSTS\d{4,7}")]
    private static partial Regex EntraCode();

    [GeneratedRegex(@"error: '([a-z_]+)'")]
    private static partial Regex OAuthError();

    [GeneratedRegex(@"^(AADSTS\d{4,7}|[a-z_]{3,40})$")]
    private static partial Regex ShownCode();
}
