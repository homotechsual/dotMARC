using System.Globalization;
using System.Security.Claims;
using DotMarc.Notifications;
using DotMarc.Security;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>Alert rules are settings for the whole estate, and a rule's scope names groups, tags and domains. So only staff with no group
/// limit may see them: someone limited to one client's groups must not learn the names of the others. The AlertsView and
/// AlertRulesManage permissions are checked separately, by policy.</summary>
public sealed class AlertRuleAccessTests
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
    public void AnUnlimitedStaffMember_MayView() => Assert.True(AlertRuleAccess.MayView(User(portal: false)));

    [Fact]
    public void AGroupLimitedStaffMember_MayNot() => Assert.False(AlertRuleAccess.MayView(User(portal: false, 3)));

    [Fact]
    public void AClientPortalUser_MayNot() => Assert.False(AlertRuleAccess.MayView(User(portal: true)));

    [Fact]
    public void AClientPortalUserLimitedToAGroup_MayNot() => Assert.False(AlertRuleAccess.MayView(User(portal: true, 3)));
}
