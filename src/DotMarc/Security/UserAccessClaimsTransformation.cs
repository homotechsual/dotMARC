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

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;

    public UserAccessClaimsTransformation(IDbContextFactory<DotMarcDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == PermissionClaimType))
        {
            // ASP.NET Core can invoke IClaimsTransformation more than once per request; this
            // check makes re-invocation a no-op instead of duplicating claims.
            return principal;
        }

        // GetObjectId() is Microsoft.Identity.Web's own accessor for the Entra object ID claim —
        // preferred over reading a raw claim type string, since it's resilient to the exact
        // claim-type mapping in effect for a given token version/configuration.
        var objectId = principal.GetObjectId();
        var email = principal.FindFirst("preferred_username")?.Value;
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
}
