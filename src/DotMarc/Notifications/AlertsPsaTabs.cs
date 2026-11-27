using System.Security.Claims;
using DotMarc.Components.Shared;
using DotMarc.Data;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.Notifications;

/// <summary>The tab list the three Alerts &amp; PSA pages render, so they agree on who sees what. Pages and the menu call <see cref="ForAsync"/>,
/// which makes the AlertsView and AlertsManage policy checks; <see cref="For"/> takes their results, and group scope is decided here.</summary>
public static class AlertsPsaTabs
{
    public const string RulesHref = "/alerts/rules";
    public const string SettingsHref = "/alerts/settings";
    public const string PsaHref = "/alerts/psa";

    public static IReadOnlyList<TabbedPageTab> For(ClaimsPrincipal user, bool canViewAlerts, bool canManageAlerts) =>
    [
        new TabbedPageTab("Rules", RulesHref, canViewAlerts && AlertRuleAccess.MayView(user)),
        new TabbedPageTab("Settings", SettingsHref, canManageAlerts),
        new TabbedPageTab("PSA", PsaHref, canManageAlerts),
    ];

    /// <summary>The same list, after making the policy checks. Every page that shows the tabs, and the menu, calls this so the checks live in one place.</summary>
    public static async Task<IReadOnlyList<TabbedPageTab>> ForAsync(ClaimsPrincipal user, IAuthorizationService authorizationService)
    {
        var canViewAlerts = (await authorizationService.AuthorizeAsync(user, nameof(Permission.AlertsView))).Succeeded;
        var canManageAlerts = (await authorizationService.AuthorizeAsync(user, nameof(Permission.AlertsManage))).Succeeded;
        return For(user, canViewAlerts, canManageAlerts);
    }
}
