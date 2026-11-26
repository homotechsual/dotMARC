using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using DotMarc.Baselines;
using DotMarc.Components.Dialogs;
using DotMarc.Components.Pages;
using DotMarc.Components.Shared;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>The Baselines pages share one tab list, and the old Sender baseline address keeps working.</summary>
public sealed class BaselinesPagesTests
{
    [Theory]
    [InlineData(null, "Follows p")]
    [InlineData(DmarcPolicyLevel.None, "Follows p (currently None)")]
    [InlineData(DmarcPolicyLevel.Quarantine, "Follows p (currently Quarantine)")]
    [InlineData(DmarcPolicyLevel.Reject, "Follows p (currently Reject)")]
    public void FollowingPolicyNamesTheEffectiveSubdomainPolicy(DmarcPolicyLevel? policy, string expected) =>
        Assert.Equal(expected, DmarcTargetText.FollowsPolicy(policy));

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
    public void BaselinesDomain_IsAtTheDomainTab_AndRequiresBaselinesManage()
    {
        Assert.Equal(["/baselines/domain"], Routes(typeof(BaselinesDomain)));
        var authorize = Assert.Single(typeof(BaselinesDomain).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(nameof(Permission.BaselinesManage), authorize.Policy);
    }

    [Fact]
    public void DmarcTargetDialog_IsNotRoutable() =>
        Assert.Empty(Routes(typeof(DmarcTargetDialog)));

    [Fact]
    public void BaselinesIndex_IsAtBaselines() =>
        Assert.Equal(["/baselines"], Routes(typeof(BaselinesIndex)));

    [Fact]
    public void AnUnlimitedPersonWithBothPermissions_SeesDomainThenSender()
    {
        var tabs = BaselineTabs.For(User(portal: false), canManageBaselines: true, canReviewSenders: true);

        Assert.Equal([("Domain", "/baselines/domain", true), ("Sender", "/baselines/sender", true)],
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

    [Theory]
    [InlineData(new[] { "BaselinesManage", "SenderReview" }, new[] { "Domain", "Sender" })]
    [InlineData(new[] { "BaselinesManage" }, new[] { "Domain" })]
    [InlineData(new[] { "SenderReview" }, new[] { "Sender" })]
    [InlineData(new string[0], new string[0])]
    public async Task ForAsync_ChecksTheBaselinesManageAndSenderReviewPolicies(string[] grantedPolicies, string[] expectedVisibleTitles)
    {
        var tabs = await BaselineTabs.ForAsync(User(portal: false), new PolicyNamedAuthorizationService(grantedPolicies));

        Assert.Equal(expectedVisibleTitles, tabs.Where(tab => tab.Visible).Select(tab => tab.Title));
    }

    [Fact]
    public async Task ForAsync_StillHidesSender_FromAGroupLimitedPerson()
    {
        var tabs = await BaselineTabs.ForAsync(User(portal: false, 3), new PolicyNamedAuthorizationService("BaselinesManage", "SenderReview"));

        Assert.Equal(["Domain"], tabs.Where(tab => tab.Visible).Select(tab => tab.Title));
    }

    [Fact]
    public void FirstVisible_IsTheFirstTabThePersonCanSee_OrNone()
    {
        var senderHidden = BaselineTabs.For(User(portal: false, 3), canManageBaselines: true, canReviewSenders: true);
        var nothingVisible = BaselineTabs.For(User(portal: true), canManageBaselines: false, canReviewSenders: false);
        var both = BaselineTabs.For(User(portal: false), canManageBaselines: true, canReviewSenders: true);
        var onlySender = BaselineTabs.For(User(portal: false), canManageBaselines: false, canReviewSenders: true);

        Assert.Equal(BaselineTabs.DomainHref, BaselineTabs.FirstVisible(senderHidden)?.Href);
        Assert.Null(BaselineTabs.FirstVisible(nothingVisible));
        Assert.Equal(BaselineTabs.DomainHref, BaselineTabs.FirstVisible(both)?.Href);
        Assert.Equal(BaselineTabs.SenderHref, BaselineTabs.FirstVisible(onlySender)?.Href);
    }

    [Theory]
    [InlineData("/senders/baseline", "/baselines/sender")]
    [InlineData("/senders/baseline/", "/baselines/sender")]
    [InlineData("/Senders/Baseline", "/baselines/sender")]
    [InlineData("/senders/baseline?group=3&x=1", "/baselines/sender?group=3&x=1")]
    public void LegacyRedirectTargets_SendsTheOldAddressToItsReplacement_KeepingTheQuery(string oldPath, string expectedDestination) =>
        Assert.Equal(expectedDestination, LegacyRedirectTargets.DestinationFor(new Uri("https://dotmarc.example" + oldPath)));

    [Theory]
    [InlineData("/baselines/sender")]
    [InlineData("/senders")]
    [InlineData("/")]
    public void LegacyRedirectTargets_GivesNullForAnAddressThatIsNotAnOldOne(string path) =>
        Assert.Null(LegacyRedirectTargets.DestinationFor(new Uri("https://dotmarc.example" + path)));

    private static readonly IReadOnlyList<TabbedPageTab> SenderAndDomainTabs =
    [
        new TabbedPageTab("Sender", "/baselines/sender", true),
        new TabbedPageTab("Domain", "/baselines/domain", true),
    ];

    [Theory]
    [InlineData("baselines/sender", 0)]
    [InlineData("baselines/domain", 1)]
    [InlineData("baselines/domain/", 1)]
    [InlineData("Baselines/Domain", 1)]
    [InlineData("baselines/domain?target=below", 1)]
    [InlineData("baselines/domain#top", 1)]
    [InlineData("baselines/other", 0)]
    [InlineData("", 0)]
    public void TabbedPageNavigation_PicksTheTabForThePath_AndFallsBackToTheFirst(string baseRelativePath, int expectedIndex) =>
        Assert.Equal(expectedIndex, TabbedPageNavigation.ActiveIndex(baseRelativePath, SenderAndDomainTabs));

    [Fact]
    public void TabbedPageNavigation_CountsOnlyTheTabsItIsGiven()
    {
        IReadOnlyList<TabbedPageTab> onlyDomain = [new TabbedPageTab("Domain", "/baselines/domain", true)];

        Assert.Equal(0, TabbedPageNavigation.ActiveIndex("baselines/domain", onlyDomain));
    }
}
