using System.Globalization;
using System.Security.Claims;
using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
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

    private static readonly string Manage = nameof(Permission.BaselinesManage);

    [Fact]
    public async Task TheAsyncChecks_NeedTheBaselinesManagePolicy_AsWellAsScope()
    {
        var staff = User(portal: false);
        var withoutPolicy = new PolicyNamedAuthorizationService();
        var withPolicy = new PolicyNamedAuthorizationService(Manage);

        Assert.False(await BaselineAccess.MayChangeSystemAsync(staff, withoutPolicy));
        Assert.False(await BaselineAccess.MayChangeGroupAsync(staff, withoutPolicy, 7));
        Assert.False(await BaselineAccess.MayChangeDomainAsync(staff, withoutPolicy, [2]));
        Assert.True(await BaselineAccess.MayChangeSystemAsync(staff, withPolicy));
        Assert.True(await BaselineAccess.MayChangeGroupAsync(staff, withPolicy, 7));
        Assert.True(await BaselineAccess.MayChangeDomainAsync(staff, withPolicy, [2]));
    }

    [Fact]
    public async Task TheAsyncChecks_StillApplyGroupScope_EvenWithThePolicy()
    {
        var staff = User(portal: false, 3, 4);
        var withPolicy = new PolicyNamedAuthorizationService(Manage);

        Assert.False(await BaselineAccess.MayChangeSystemAsync(staff, withPolicy));
        Assert.True(await BaselineAccess.MayChangeGroupAsync(staff, withPolicy, 3));
        Assert.False(await BaselineAccess.MayChangeGroupAsync(staff, withPolicy, 5));
        Assert.True(await BaselineAccess.MayChangeDomainAsync(staff, withPolicy, [4, 9]));
        Assert.False(await BaselineAccess.MayChangeDomainAsync(staff, withPolicy, [9]));
    }

    [Fact]
    public async Task TheAsyncChecks_RefuseAClientPortalUser_EvenWithThePolicy()
    {
        var portalUser = User(portal: true, 3);
        var withPolicy = new PolicyNamedAuthorizationService(Manage);

        Assert.False(await BaselineAccess.MayChangeSystemAsync(portalUser, withPolicy));
        Assert.False(await BaselineAccess.MayChangeGroupAsync(portalUser, withPolicy, 3));
        Assert.False(await BaselineAccess.MayChangeDomainAsync(portalUser, withPolicy, [3]));
    }
}
