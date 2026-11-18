using System.Security.Claims;
using DotMarc.Portal;

namespace DotMarc.Reporting;

/// <summary>Who may change sender decisions at the shared levels. These are about group scope only. The SenderReview permission is
/// checked separately, by policy.</summary>
public static class SenderDecisionAccess
{
    /// <summary>The baseline applies to every domain, so only staff with no group limit may change it.</summary>
    public static bool MayChangeBaseline(ClaimsPrincipal user) =>
        !ClientPortalGate.IsPortalUser(user) && PortalData.ScopedGroupIds(user).Count == 0;

    /// <summary>Staff limited to some groups may change only those groups' defaults. Staff with no limit may change any group's.</summary>
    public static bool MayChangeGroup(ClaimsPrincipal user, int groupId) =>
        !ClientPortalGate.IsPortalUser(user) && PortalScope.CanPreview(user, groupId);
}
