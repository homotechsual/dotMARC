using System.Globalization;
using System.Security.Claims;

namespace DotMarc.Portal;

/// <summary>Which Groups' domains the portal pages show, and where their links go. A portal user's own scope comes from
/// their claims; staff previewing a Group as its clients see it get one from the preview page.</summary>
public sealed record PortalScope(IReadOnlyCollection<int> GroupIds, string? PreviewOf, string HomeHref)
{
    public const string PreviewPathPrefix = "/portal/preview/";

    public string DomainLinkPrefix => $"{HomeHref}/domains/";

    public static PortalScope ForUser(ClaimsPrincipal user) => new(PortalData.ScopedGroupIds(user), null, "/portal");

    public static PortalScope Preview(int groupId, string groupName) =>
        new([groupId], groupName, PreviewPathPrefix + groupId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Staff limited to some Groups can preview only those; unscoped staff can preview any Group.</summary>
    public static bool CanPreview(ClaimsPrincipal user, int groupId)
    {
        var scopedGroupIds = PortalData.ScopedGroupIds(user);
        return scopedGroupIds.Count == 0 || scopedGroupIds.Contains(groupId);
    }

    /// <summary>The Group a preview URL is for, such as 7 from /portal/preview/7/domains/example.com.</summary>
    public static int? PreviewGroupId(string absolutePath)
    {
        if (!absolutePath.StartsWith(PreviewPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segment = absolutePath[PreviewPathPrefix.Length..].Split('/', 2)[0];
        return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var groupId) ? groupId : null;
    }
}
