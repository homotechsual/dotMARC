using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using DotMarc.Baselines;
using DotMarc.Components.Pages;
using DotMarc.Security;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>The Baselines pages share one tab list, and the old Sender baseline address keeps working.</summary>
public sealed class BaselinesPagesTests
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

    private static List<string> Routes(Type page) =>
        page.GetCustomAttributes<RouteAttribute>().Select(route => route.Template).ToList();

    [Fact]
    public void SenderDefaults_IsAtTheBaselinesSenderTab_AndTheGroupView_ButNotTheOldAddress()
    {
        var routes = Routes(typeof(SenderDefaults));

        Assert.Contains("/baselines/sender", routes);
        Assert.Contains("/groups/{GroupId:int}/senders", routes);
        Assert.DoesNotContain("/senders/baseline", routes);
    }

    [Fact]
    public void LegacyRedirects_CoversTheOldSenderBaselineAddress() =>
        Assert.Contains("/senders/baseline", Routes(typeof(LegacyRedirects)));

    [Fact]
    public void BaselinesIndex_IsAtBaselines() =>
        Assert.Equal(["/baselines"], Routes(typeof(BaselinesIndex)));

    [Fact]
    public void AnUnlimitedPersonWithBothPermissions_SeesSenderThenDomain()
    {
        var tabs = BaselineTabs.For(User(portal: false), canManageBaselines: true, canReviewSenders: true);

        Assert.Equal([("Sender", "/baselines/sender", true), ("Domain", "/baselines/domain", true)],
            tabs.Select(tab => (tab.Title, tab.Href, tab.Visible)));
    }

    [Fact]
    public void AGroupLimitedPersonWithBaselinesManage_SeesOnlyDomain()
    {
        var tabs = BaselineTabs.For(User(portal: false, 3), canManageBaselines: true, canReviewSenders: true);

        Assert.Equal(["Domain"], tabs.Where(tab => tab.Visible).Select(tab => tab.Title));
    }

    [Fact]
    public void AnUnlimitedPersonWithOnlySenderReview_SeesOnlySender()
    {
        var tabs = BaselineTabs.For(User(portal: false), canManageBaselines: false, canReviewSenders: true);

        Assert.Equal(["Sender"], tabs.Where(tab => tab.Visible).Select(tab => tab.Title));
    }

    [Fact]
    public void AClientPortalUser_SeesNoTabs()
    {
        var tabs = BaselineTabs.For(User(portal: true), canManageBaselines: false, canReviewSenders: false);

        Assert.DoesNotContain(tabs, tab => tab.Visible);
    }
}
