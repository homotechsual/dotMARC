using System.Globalization;
using System.Security.Claims;
using DotMarc.Data;

namespace DotMarc.Security;

/// <summary>The permission and scoped-group claims for a role and its groups, built the same way for a signed-in
/// person and an API key so the two can never be authorised differently. Groups count only on a scopable role. An API
/// key never gets AccessManage, even if its role gains it after the key was made.</summary>
public static class AccessClaims
{
    public static IEnumerable<Claim> For(Role role, IEnumerable<int> scopedGroupIds, bool forApiKey = false)
    {
        foreach (var permission in role.Permissions)
        {
            if (forApiKey && permission == Permission.AccessManage)
            {
                continue;
            }

            yield return new Claim(UserAccessClaimsTransformation.PermissionClaimType, permission.ToString());
        }

        if (!role.IsScopable)
        {
            yield break;
        }

        foreach (var groupId in scopedGroupIds)
        {
            yield return new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, groupId.ToString(CultureInfo.InvariantCulture));
        }
    }
}
