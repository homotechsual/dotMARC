using System.Globalization;
using System.Security.Claims;
using DotMarc.Reporting.ClientReports;
using DotMarc.Security;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

/// <summary>Group scope for sending and downloading reports (the ReportsManage permission is checked by policy).</summary>
public sealed class ClientReportAccessTests
{
    private static ClaimsPrincipal User(bool portal, params int[] scopedGroupIds)
    {
        var claims = scopedGroupIds.Select(groupId => new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, groupId.ToString(CultureInfo.InvariantCulture))).ToList();
        if (portal)
        {
            claims.Add(new Claim(UserAccessClaimsTransformation.ClientPortalClaimType, "true"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public void UnscopedStaff_MayManageAnyGroup() => Assert.True(ClientReportAccess.MayManage(User(portal: false), 7));

    [Fact]
    public void ScopedStaff_MayManageOnlyTheirGroups()
    {
        var staff = User(portal: false, 3);

        Assert.Equal((true, false), (ClientReportAccess.MayManage(staff, 3), ClientReportAccess.MayManage(staff, 7)));
    }

    [Fact]
    public void APortalUser_MayManageNothing_EvenTheirOwnGroup() => Assert.False(ClientReportAccess.MayManage(User(portal: true, 3), 3));
}
