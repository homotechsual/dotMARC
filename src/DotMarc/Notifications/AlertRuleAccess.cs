using System.Security.Claims;
using DotMarc.Portal;

namespace DotMarc.Notifications;

/// <summary>Who may see the alert rules. They are settings for the whole estate, and a rule's scope names groups, tags and domains, so
/// someone limited to some groups (or a client portal user) must not see them. The permissions that let staff view and change rules
/// are checked separately, by policy.</summary>
public static class AlertRuleAccess
{
    public static bool MayView(ClaimsPrincipal user) =>
        !ClientPortalGate.IsPortalUser(user) && PortalData.ScopedGroupIds(user).Count == 0;
}
