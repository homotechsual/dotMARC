using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Portal;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.Baselines;

/// <summary>Who may change a DMARC target. The scope methods decide by group scope only; the Async methods add the BaselinesManage policy, and are what
/// every page calls just before it shows a target dialog and again just before it saves, so a change of access between the two is caught.</summary>
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

    public static async Task<bool> MayChangeSystemAsync(ClaimsPrincipal user, IAuthorizationService authorizationService) =>
        await HasBaselinesManageAsync(user, authorizationService) && MayChangeSystem(user);

    public static async Task<bool> MayChangeGroupAsync(ClaimsPrincipal user, IAuthorizationService authorizationService, int groupId) =>
        await HasBaselinesManageAsync(user, authorizationService) && MayChangeGroup(user, groupId);

    public static async Task<bool> MayChangeDomainAsync(ClaimsPrincipal user, IAuthorizationService authorizationService, IReadOnlyCollection<int> domainGroupIds) =>
        await HasBaselinesManageAsync(user, authorizationService) && MayChangeDomain(user, domainGroupIds);

    private static async Task<bool> HasBaselinesManageAsync(ClaimsPrincipal user, IAuthorizationService authorizationService) =>
        (await authorizationService.AuthorizeAsync(user, nameof(Permission.BaselinesManage))).Succeeded;
}
