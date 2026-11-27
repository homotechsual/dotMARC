using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using DotMarc.Components.Pages;
using DotMarc.Components.Shared;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>Alert rules, alert settings and PSA settings share one tab list, and the old PSA settings address keeps working.</summary>
public sealed class AlertsPsaPagesTests
{
    private static ClaimsPrincipal User(params int[] scopedGroupIds)
    {
        var claims = scopedGroupIds.Select(groupId => new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, groupId.ToString(CultureInfo.InvariantCulture))).ToList();
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static List<string> Routes(Type page) =>
        page.GetCustomAttributes<RouteAttribute>().Select(route => route.Template).ToList();

    private static IEnumerable<string> VisibleTitles(IReadOnlyList<TabbedPageTab> tabs) =>
        tabs.Where(tab => tab.Visible).Select(tab => tab.Title);

    [Fact]
    public void AnUnlimitedPersonWithBothPermissions_SeesRulesThenSettingsThenPsa()
    {
        var tabs = AlertsPsaTabs.For(User(), canViewAlerts: true, canManageAlerts: true);

        Assert.Equal([("Rules", "/alerts/rules", true), ("Settings", "/alerts/settings", true), ("PSA", "/alerts/psa", true)],
            tabs.Select(tab => (tab.Title, tab.Href, tab.Visible)));
    }

    [Fact]
    public void AlertsManageAlone_SeesSettingsAndPsa() =>
        Assert.Equal(["Settings", "PSA"], VisibleTitles(AlertsPsaTabs.For(User(), canViewAlerts: false, canManageAlerts: true)));

    [Fact]
    public void AlertsViewAlone_SeesOnlyRules() =>
        Assert.Equal(["Rules"], VisibleTitles(AlertsPsaTabs.For(User(), canViewAlerts: true, canManageAlerts: false)));

    [Fact]
    public void AGroupLimitedPersonWithAlertsView_SeesNoRulesTab() =>
        Assert.DoesNotContain("Rules", VisibleTitles(AlertsPsaTabs.For(User(3), canViewAlerts: true, canManageAlerts: false)));

    [Theory]
    [InlineData(new[] { "AlertsView", "AlertsManage" }, new[] { "Rules", "Settings", "PSA" })]
    [InlineData(new[] { "AlertsManage" }, new[] { "Settings", "PSA" })]
    [InlineData(new[] { "AlertsView" }, new[] { "Rules" })]
    [InlineData(new string[0], new string[0])]
    public async Task ForAsync_ChecksTheAlertsViewAndAlertsManagePolicies(string[] grantedPolicies, string[] expectedVisibleTitles)
    {
        var tabs = await AlertsPsaTabs.ForAsync(User(), new PolicyNamedAuthorizationService(grantedPolicies));

        Assert.Equal(expectedVisibleTitles, VisibleTitles(tabs));
    }

    [Fact]
    public void FirstVisible_IsTheFirstVisibleTab_OrNone()
    {
        var settingsFirst = AlertsPsaTabs.For(User(), canViewAlerts: false, canManageAlerts: true);
        var nothing = AlertsPsaTabs.For(User(), canViewAlerts: false, canManageAlerts: false);

        Assert.Equal(AlertsPsaTabs.SettingsHref, TabbedPageNavigation.FirstVisible(settingsFirst)?.Href);
        Assert.Null(TabbedPageNavigation.FirstVisible(nothing));
        Assert.Null(TabbedPageNavigation.FirstVisible([]));
    }

    [Fact]
    public void PsaSettings_IsAtTheAlertsPsaTab_ButNotTheOldAddress_AndRequiresAlertsManage()
    {
        var routes = Routes(typeof(PsaSettings));

        Assert.Contains("/alerts/psa", routes);
        Assert.DoesNotContain("/psa/settings", routes);
        var authorize = Assert.Single(typeof(PsaSettings).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(nameof(Permission.AlertsManage), authorize.Policy);
    }

    [Fact]
    public void TheOtherTabs_KeepTheirAddressesAndPermissions()
    {
        Assert.Equal(["/alerts/rules"], Routes(typeof(AlertRules)));
        Assert.Equal(nameof(Permission.AlertsView), Assert.Single(typeof(AlertRules).GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Equal(["/alerts/settings"], Routes(typeof(AlertsSettings)));
        Assert.Equal(nameof(Permission.AlertsManage), Assert.Single(typeof(AlertsSettings).GetCustomAttributes<AuthorizeAttribute>()).Policy);
    }

    [Fact]
    public void LegacyRedirects_CoversTheOldPsaSettingsAddress() =>
        Assert.Contains("/psa/settings", Routes(typeof(LegacyRedirects)));

    [Fact]
    public void LegacyRedirectTargets_SendsOldPsaSettingsToTheAlertsPsaTab_KeepingTheQuery() =>
        Assert.Equal("/alerts/psa?psa=ninjaone&ninjaone=connected",
            LegacyRedirectTargets.DestinationFor(new Uri("https://x/psa/settings?psa=ninjaone&ninjaone=connected")));
}
