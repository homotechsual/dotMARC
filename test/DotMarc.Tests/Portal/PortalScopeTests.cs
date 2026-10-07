using System.Security.Claims;
using DotMarc.Portal;
using DotMarc.Security;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalScopeTests
{
    private static ClaimsPrincipal UserScopedTo(params int[] groupIds) => new(new ClaimsIdentity(
        groupIds.Select(groupId => new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, groupId.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        "Test"));

    [Fact]
    public void UnscopedStaff_CanPreviewAnyGroup() => Assert.True(PortalScope.CanPreview(UserScopedTo(), 7));

    [Fact]
    public void StaffLimitedToGroups_CanPreviewOnlyThoseGroups()
    {
        var user = UserScopedTo(3, 4);

        Assert.Equal((true, false), (PortalScope.CanPreview(user, 4), PortalScope.CanPreview(user, 7)));
    }

    [Fact]
    public void APreview_LinksToItsOwnDomainPages()
    {
        var scope = PortalScope.Preview(7, "Aurora Retail");

        Assert.Equal(("/portal/preview/7", "/portal/preview/7/domains/", "Aurora Retail"), (scope.HomeHref, scope.DomainLinkPrefix, scope.PreviewOf));
    }
}
