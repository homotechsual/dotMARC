using System.Security.Claims;

namespace DotMarc.Security;

/// <summary>Reads identity details from a signed-in user's claims, shared by the claims transformation and the
/// audit log so both find the same email.</summary>
public static class UserClaims
{
    /// <summary>Which claim carries the email varies by tenant and token version, and getting it wrong would lock
    /// people out, so this tries preferred_username first (right for the v2.0 delegated flow dotMARC uses), then
    /// the UPN and Email claim types Microsoft.Identity.Web maps for some configurations, then a literal "email"
    /// claim some tenants send instead. An empty claim falls through to the next candidate.</summary>
    public static string? GetEmail(ClaimsPrincipal principal)
    {
        foreach (var claimType in new[] { "preferred_username", ClaimTypes.Upn, ClaimTypes.Email, "email" })
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }
}
