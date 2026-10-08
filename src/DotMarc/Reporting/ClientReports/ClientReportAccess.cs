using System.Security.Claims;
using DotMarc.Portal;

namespace DotMarc.Reporting.ClientReports;

public static class ClientReportAccess
{
    /// <summary>Staff limited to some Groups manage reports only for those; unscoped staff for any Group. The same rule
    /// as previewing a Group's portal. The ReportsManage permission is checked separately, by policy.</summary>
    public static bool MayManage(ClaimsPrincipal user, int groupId) =>
        !ClientPortalGate.IsPortalUser(user) && PortalScope.CanPreview(user, groupId);
}
