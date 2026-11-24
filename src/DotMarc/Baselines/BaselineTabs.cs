using System.Security.Claims;
using DotMarc.Components.Shared;
using DotMarc.Reporting;

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
}
