using System.Globalization;
using System.Security.Claims;
using DotMarc.Baselines;
using DotMarc.Security;
using Xunit;

namespace DotMarc.Tests.Baselines;

/// <summary>Who may change the system, a group's and a domain's DMARC target. The BaselinesManage permission is checked separately, by policy.</summary>
public sealed class BaselineAccessTests
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
    public void AnUnlimitedPerson_MayChangeTheSystemTarget_AnyGroup_AndAnyDomain()
    {
        var staff = User(portal: false);

        Assert.True(BaselineAccess.MayChangeSystem(staff));
        Assert.True(BaselineAccess.MayChangeGroup(staff, 7));
        Assert.True(BaselineAccess.MayChangeDomain(staff, [2, 5]));
        Assert.True(BaselineAccess.MayChangeDomain(staff, []));
    }

    [Fact]
    public void AGroupLimitedPerson_MayNotChangeTheSystemTarget() =>
        Assert.False(BaselineAccess.MayChangeSystem(User(portal: false, 3, 4)));

    [Fact]
    public void AGroupLimitedPerson_MayChangeOnlyTheirOwnGroups()
    {
        var staff = User(portal: false, 3, 4);

        Assert.True(BaselineAccess.MayChangeGroup(staff, 3));
        Assert.False(BaselineAccess.MayChangeGroup(staff, 5));
    }

    [Fact]
    public void AGroupLimitedPerson_MayChangeADomainOnlyWhenOneOfItsGroupsIsTheirs()
    {
        var staff = User(portal: false, 3, 4);

        Assert.True(BaselineAccess.MayChangeDomain(staff, [4, 9]));
        Assert.False(BaselineAccess.MayChangeDomain(staff, [9]));
        Assert.False(BaselineAccess.MayChangeDomain(staff, []));
    }

    [Fact]
    public void AClientPortalUser_MayChangeNothing()
    {
        var portalUser = User(portal: true, 3);

        Assert.False(BaselineAccess.MayChangeSystem(User(portal: true)));
        Assert.False(BaselineAccess.MayChangeGroup(portalUser, 3));
        Assert.False(BaselineAccess.MayChangeDomain(portalUser, [3]));
        Assert.False(BaselineAccess.MayChangeDomain(User(portal: true), []));
    }
}
