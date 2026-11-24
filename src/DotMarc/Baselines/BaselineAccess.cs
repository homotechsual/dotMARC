using System.Security.Claims;
using DotMarc.Portal;

namespace DotMarc.Baselines;

/// <summary>Who may change a DMARC target, by group scope only. The BaselinesManage permission is checked separately, by policy.</summary>
public static class BaselineAccess
{
    /// <summary>The system target applies to every domain, so only staff with no group limit may change it.</summary>
    public static bool MayChangeSystem(ClaimsPrincipal user) =>
        !ClientPortalGate.IsPortalUser(user) && PortalData.ScopedGroupIds(user).Count == 0;

    /// <summary>Staff limited to some groups may change only those groups' targets. Staff with no limit may change any group's.</summary>
    public static bool MayChangeGroup(ClaimsPrincipal user, int groupId) =>
        !ClientPortalGate.IsPortalUser(user) && PortalScope.CanPreview(user, groupId);

    /// <summary>A domain may be changed by unlimited staff, or by limited staff when one of its groups is theirs.</summary>
    public static bool MayChangeDomain(ClaimsPrincipal user, IReadOnlyCollection<int> domainGroupIds)
    {
        if (ClientPortalGate.IsPortalUser(user))
        {
            return false;
        }

        return PortalData.ScopedGroupIds(user).Count == 0 || domainGroupIds.Any(groupId => PortalScope.CanPreview(user, groupId));
    }
}
