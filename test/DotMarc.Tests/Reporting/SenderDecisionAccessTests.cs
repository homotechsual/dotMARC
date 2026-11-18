using System.Globalization;
using System.Security.Claims;
using DotMarc.Reporting;
using DotMarc.Security;
using Xunit;

namespace DotMarc.Tests.Reporting;

/// <summary>Who may change the baseline and a group's sender defaults. The SenderReview permission is checked separately, by policy.</summary>
public sealed class SenderDecisionAccessTests
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
    public void AnUnlimitedPerson_MayChangeTheBaseline_AndAnyGroup()
    {
        var staff = User(portal: false);

        Assert.True(SenderDecisionAccess.MayChangeBaseline(staff));
        Assert.True(SenderDecisionAccess.MayChangeGroup(staff, 7));
    }

    [Fact]
    public void AGroupLimitedPerson_MayNotChangeTheBaseline() =>
        Assert.False(SenderDecisionAccess.MayChangeBaseline(User(portal: false, 3)));

    [Fact]
    public void AGroupLimitedPerson_MayChangeOnlyTheirOwnGroups()
    {
        var staff = User(portal: false, 3, 4);

        Assert.True(SenderDecisionAccess.MayChangeGroup(staff, 3));
        Assert.True(SenderDecisionAccess.MayChangeGroup(staff, 4));
        Assert.False(SenderDecisionAccess.MayChangeGroup(staff, 7));
    }

    [Fact]
    public void AClientPortalUser_MayChangeNothing_WithOrWithoutAGroupScope()
    {
        Assert.False(SenderDecisionAccess.MayChangeBaseline(User(portal: true)));
        Assert.False(SenderDecisionAccess.MayChangeGroup(User(portal: true), 3));
        Assert.False(SenderDecisionAccess.MayChangeGroup(User(portal: true, 3), 3));
    }
}
