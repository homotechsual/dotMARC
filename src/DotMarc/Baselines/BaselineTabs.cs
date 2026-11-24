using System.Security.Claims;
using DotMarc.Components.Shared;
using DotMarc.Data;
using DotMarc.Reporting;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.Baselines;

/// <summary>The tab list both Baselines pages render, so they agree on who sees what. The booleans are the results of the BaselinesManage and
/// SenderReview policy checks, which the page makes; group scope is decided here.</summary>
public static class BaselineTabs
{
    public const string SenderHref = "/baselines/sender";
    public const string DomainHref = "/baselines/domain";

    public static IReadOnlyList<TabbedPageTab> For(ClaimsPrincipal user, bool canManageBaselines, bool canReviewSenders) =>
    [
        new TabbedPageTab("Sender", SenderHref, canReviewSenders && SenderDecisionAccess.MayChangeBaseline(user)),
        new TabbedPageTab("Domain", DomainHref, canManageBaselines),
    ];

    /// <summary>The same list, after making the policy checks. Every page that shows the tabs, and the menu, calls this so the checks live in one place.</summary>
    public static async Task<IReadOnlyList<TabbedPageTab>> ForAsync(ClaimsPrincipal user, IAuthorizationService authorizationService)
    {
        var canManageBaselines = (await authorizationService.AuthorizeAsync(user, nameof(Permission.BaselinesManage))).Succeeded;
        var canReviewSenders = (await authorizationService.AuthorizeAsync(user, nameof(Permission.SenderReview))).Succeeded;
        return For(user, canManageBaselines, canReviewSenders);
    }
}
