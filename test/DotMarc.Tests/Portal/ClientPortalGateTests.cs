using System.Security.Claims;
using DotMarc.Portal;
using DotMarc.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace DotMarc.Tests.Portal;

/// <summary>The gate on its own, for the resources the web tests can't reach: Blazor's circuit endpoints and in-circuit
/// page checks, which pass route data instead of an HttpContext.</summary>
public sealed class ClientPortalGateTests
{
    // dotMARC's fallback policy: signed in, with at least one permission.
    private static IAuthorizationRequirement[] FallbackRequirements() =>
    [
        new DenyAnonymousAuthorizationRequirement(),
        new ClaimsAuthorizationRequirement(UserAccessClaimsTransformation.PermissionClaimType, null),
    ];

    private static ClaimsPrincipal PortalUserWithoutPermissions() => new(new ClaimsIdentity(
        [new Claim(UserAccessClaimsTransformation.ClientPortalClaimType, "true"), new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, "1")],
        "Test"));

    private static HttpContext RequestTo(string path) => new DefaultHttpContext { Request = { Path = path } };

    private static async Task<AuthorizationHandlerContext> EvaluateAsync(ClaimsPrincipal user, object? resource)
    {
        var context = new AuthorizationHandlerContext(FallbackRequirements(), user, resource);
        await new ClientPortalGate().HandleAsync(context);
        return context;
    }

    [Theory]
    [InlineData("/_blazor")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/_blazor/initializers")]
    public async Task APortalUser_WhoseRoleHasNoPermissions_StillGetsACircuit(string path)
    {
        var context = await EvaluateAsync(PortalUserWithoutPermissions(), RequestTo(path));

        Assert.True(context.HasSucceeded);
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/_blazorish")]
    public async Task APortalUser_IsRefusedOtherEndpoints(string path)
    {
        var context = await EvaluateAsync(PortalUserWithoutPermissions(), RequestTo(path));

        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task APortalUser_IsRefusedAnInternalPage_CheckedInsideTheCircuit()
    {
        var context = await EvaluateAsync(PortalUserWithoutPermissions(), new RouteData());

        Assert.True(context.HasFailed);
    }

    [Theory]
    [InlineData(typeof(DotMarc.Components.Pages.Portal.PortalHome), true)]
    [InlineData(typeof(DotMarc.Components.Pages.Portal.PortalDomain), true)]
    [InlineData(typeof(DotMarc.Components.Pages.Demo.DemoSignIn), true)] // anonymous, so switching demo persona still works
    [InlineData(typeof(DotMarc.Components.Pages.Portal.PortalPreview), false)]
    [InlineData(typeof(DotMarc.Components.Pages.Dashboard), false)]
    [InlineData(typeof(DotMarc.Components.Pages.Home), false)] // relies on the fallback policy alone
    public void APortalUser_MayOnlyRenderPortalAndAnonymousPages(Type pageType, bool allowed) =>
        Assert.Equal(allowed, ClientPortalGate.AllowsPortalUserOn(pageType));

    [Fact]
    public void EveryRoutablePage_IsEitherAPortalPage_OrKeptFromPortalUsers()
    {
        var pages = typeof(Program).Assembly.GetTypes()
            .Where(type => type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: false).Length > 0)
            .ToList();

        var allowed = pages.Where(ClientPortalGate.AllowsPortalUserOn).Select(type => type.Name).OrderBy(name => name);

        Assert.Equal(["AccessDenied", "DemoSignIn", "Error", "PortalDomain", "PortalHome"], allowed);
    }

    [Fact]
    public async Task Staff_AreLeftToTheUsualHandlers()
    {
        var staff = new ClaimsPrincipal(new ClaimsIdentity([new Claim(UserAccessClaimsTransformation.PermissionClaimType, "DomainsView")], "Test"));

        var context = await EvaluateAsync(staff, RequestTo("/_blazor/negotiate"));

        Assert.False(context.HasFailed);
        Assert.Equal(2, context.PendingRequirements.Count());
    }
}
