using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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

    private static IAuthorizationService AuthorizationWithGroupsWritePolicy()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        Microsoft.Extensions.DependencyInjection.PolicyServiceCollectionExtensions.AddAuthorization(services, options =>
            options.AddPolicy(PortalScope.PreviewPolicyName, policy => policy.RequireClaim(UserAccessClaimsTransformation.PermissionClaimType, "GroupsAdd")));
        return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IAuthorizationService>(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));
    }

    private static ClaimsPrincipal Staff(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public async Task StaffWhoCanManageGroups_MayPreview()
    {
        var staff = Staff(new Claim(UserAccessClaimsTransformation.PermissionClaimType, "GroupsAdd"));

        Assert.True(await PortalScope.MayPreviewAsync(AuthorizationWithGroupsWritePolicy(), staff, 7));
    }

    [Fact]
    public async Task StaffWhoCantManageGroups_MayNotPreview()
    {
        var staff = Staff(new Claim(UserAccessClaimsTransformation.PermissionClaimType, "DomainsView"));

        Assert.False(await PortalScope.MayPreviewAsync(AuthorizationWithGroupsWritePolicy(), staff, 7));
    }

    [Fact]
    public async Task APortalUser_MayNotPreview_WhateverTheirPermissions()
    {
        var portalUser = Staff(
            new Claim(UserAccessClaimsTransformation.PermissionClaimType, "GroupsAdd"),
            new Claim(UserAccessClaimsTransformation.ClientPortalClaimType, "true"));

        Assert.False(await PortalScope.MayPreviewAsync(AuthorizationWithGroupsWritePolicy(), portalUser, 7));
    }

    [Fact]
    public void APreview_LinksToItsOwnDomainPages()
    {
        var scope = PortalScope.Preview(7, "Aurora Retail");

        Assert.Equal(("/portal/preview/7", "/portal/preview/7/domains/", "Aurora Retail"), (scope.HomeHref, scope.DomainLinkPrefix, scope.PreviewOf));
    }
}
