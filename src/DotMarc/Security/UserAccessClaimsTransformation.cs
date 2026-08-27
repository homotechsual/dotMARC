using System.Security.Claims;
using DotMarc.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

namespace DotMarc.Security;

/// <summary>Enriches the signed-in user's ClaimsPrincipal with dotMARC-specific authorization
/// data — one claim per granted Permission, plus one claim per accessible Group ID when the
/// grant is scoped — looked up via UserAccessManagementService.ResolveAsync. ASP.NET Core
/// invokes IClaimsTransformation as part of the authentication middleware, once per sign-in,
/// before the Blazor Server circuit starts — not on every render — so this doesn't add a
/// database round-trip to normal page navigation.</summary>
public sealed class UserAccessClaimsTransformation : IClaimsTransformation
{
    public const string PermissionClaimType = "dotmarc:permission";
    public const string ScopedGroupClaimType = "dotmarc:scoped-group";

    // Marks a principal as already having been through resolution below, independent of how
    // many (if any) PermissionClaimType/ScopedGroupClaimType claims that resolution produced. A
    // scopable Role with an empty Permissions list resolves to zero permission claims but N
    // group claims, so the idempotency guard can't infer "already transformed" from
    // PermissionClaimType's presence alone — that under-counts and lets a second invocation
    // re-add the group claims, duplicating them.
    private const string ResolvedClaimType = "dotmarc:access-resolved";

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;

    public UserAccessClaimsTransformation(IDbContextFactory<DotMarcDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == ResolvedClaimType))
        {
            // ASP.NET Core can invoke IClaimsTransformation more than once per request; this
            // check makes re-invocation a no-op instead of duplicating claims.
            return principal;
        }

        // GetObjectId() is Microsoft.Identity.Web's own accessor for the Entra object ID claim —
        // preferred over reading a raw claim type string, since it's resilient to the exact
        // claim-type mapping in effect for a given token version/configuration.
        var objectId = principal.GetObjectId();

        // Which claim actually carries the signed-in user's email can't be verified without a
        // live Entra sign-in (none available in this environment), and getting it wrong would
        // silently lock out every user forever — ResolveAsync's email fallback path would never
        // match a granted UserAccess row. preferred_username is correct for the common
        // v2.0-token/delegated-flow case this app uses, but this fallback chain reduces the blast
        // radius of that specific claim being absent or empty for some tenant/token
        // configuration: try preferred_username first, then the UPN and Email claim types
        // (populated by Microsoft.Identity.Web's default claim mapping for some configurations),
        // then a literal "email" claim type some tenants emit instead.
        var email = FirstNonEmptyClaim(principal, "preferred_username", ClaimTypes.Upn, ClaimTypes.Email, "email");
        if (string.IsNullOrEmpty(objectId) && string.IsNullOrEmpty(email))
        {
            return principal;
        }

        await using var context = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var access = await UserAccessManagementService.ResolveAsync(context, objectId, email, CancellationToken.None).ConfigureAwait(false);
        if (access is null)
        {
            return principal;
        }

        var identity = (ClaimsIdentity)principal.Identity;
        identity.AddClaim(new Claim(ResolvedClaimType, "true"));
        foreach (var permission in access.Role.Permissions)
        {
            identity.AddClaim(new Claim(PermissionClaimType, permission.ToString()));
        }
        foreach (var group in access.ScopedGroups)
        {
            identity.AddClaim(new Claim(ScopedGroupClaimType, group.Id.ToString()));
        }

        return principal;
    }

    /// <summary>Returns the value of the first of the given claim types that's present with a
    /// non-empty value, or null if none are. Distinct from FindFirst(...)?.Value on a single
    /// claim type: a claim present but empty-valued should still fall through to the next
    /// candidate rather than short-circuiting the chain with an unusable value.</summary>
    private static string? FirstNonEmptyClaim(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
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
