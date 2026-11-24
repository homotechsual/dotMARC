using System.Security.Claims;
using DotMarc.Components.Shared;
using DotMarc.Data;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.ServerLogs;

/// <summary>The tab list the two Logs pages render, so they agree on who sees what. Pages and the menu call <see cref="ForAsync"/>, which makes
/// the LogsView and AuditView policy checks; <see cref="For"/> takes their results.</summary>
public static class LogsTabs
{
    public const string ServerHref = "/logs/server";
    public const string AuditHref = "/logs/audit";

    public static IReadOnlyList<TabbedPageTab> For(ClaimsPrincipal user, bool canViewServerLogs, bool canViewAudit) =>
    [
        new TabbedPageTab("Server", ServerHref, canViewServerLogs),
        new TabbedPageTab("Audit", AuditHref, canViewAudit),
    ];

    /// <summary>The same list, after making the policy checks. Every page that shows the tabs, and the menu, calls this so the checks live in one place.</summary>
    public static async Task<IReadOnlyList<TabbedPageTab>> ForAsync(ClaimsPrincipal user, IAuthorizationService authorizationService)
    {
        var canViewServerLogs = (await authorizationService.AuthorizeAsync(user, nameof(Permission.LogsView))).Succeeded;
        var canViewAudit = (await authorizationService.AuthorizeAsync(user, nameof(Permission.AuditView))).Succeeded;
        return For(user, canViewServerLogs, canViewAudit);
    }
}
