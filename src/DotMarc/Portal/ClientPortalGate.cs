using DotMarc.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.Portal;

/// <summary>Marks a policy as one client portal users may pass. Every other policy fails for them.</summary>
public sealed class ClientPortalRequirement : IAuthorizationRequirement;

/// <summary>Keeps client portal users inside the portal. It runs for every policy (named, fallback, AuthorizeView and
/// API ones alike) and fails any that isn't the portal's own, so a portal user can't open an internal page, call the
/// API or change anything, whatever their role allows. It also satisfies the portal requirement for portal users.</summary>
public sealed class ClientPortalGate : IAuthorizationHandler
{
    public static bool IsPortalUser(System.Security.Claims.ClaimsPrincipal user) =>
        user.HasClaim(UserAccessClaimsTransformation.ClientPortalClaimType, "true");

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        var isPortalPolicy = context.Requirements.OfType<ClientPortalRequirement>().Any();
        if (!IsPortalUser(context.User))
        {
            // Not a portal user: the portal's own policy fails because its requirement is never met.
            return Task.CompletedTask;
        }

        if (!isPortalPolicy && IsPortalPlumbing(context.Resource))
        {
            // The fallback policy behind these also wants a permission claim, which a portal grant's role may not have
            // (a role made just for clients needs none). The portal needs the circuit and sign out regardless.
            foreach (var requirement in context.PendingRequirements.ToList())
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        if (!isPortalPolicy)
        {
            context.Fail(new AuthorizationFailureReason(this, "Client portal users can only use the portal."));
            return Task.CompletedTask;
        }

        foreach (var requirement in context.PendingRequirements.OfType<ClientPortalRequirement>().ToList())
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    // Blazor's own circuit endpoints and sign out sit behind the fallback and default policies, but the portal needs
    // them: without the circuit nothing on a portal page is interactive, and a portal user must be able to sign out.
    private static bool IsPortalPlumbing(object? resource) =>
        resource is HttpContext httpContext
        && (httpContext.Request.Path.StartsWithSegments("/_blazor") || httpContext.Request.Path.StartsWithSegments("/signout"));
}

/// <summary>Where a denied request goes: a portal user to the portal, anyone else denied the portal to the dashboard,
/// everyone else to the usual access denied page. API requests get a plain 403.</summary>
public static class ClientPortalRedirects
{
    public static Task OnRedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        var target = ClientPortalGate.IsPortalUser(context.HttpContext.User) ? "/portal"
            : context.Request.Path.StartsWithSegments("/portal") ? "/dashboard"
            : context.RedirectUri;
        context.Response.Redirect(target);
        return Task.CompletedTask;
    }
}
