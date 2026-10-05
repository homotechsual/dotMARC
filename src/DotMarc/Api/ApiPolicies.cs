using DotMarc.Data;
using DotMarc.Security;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.Api;

/// <summary>The API's own copy of the permission policies, naming only the ApiKey scheme. The UI's policies use the
/// default (cookie) scheme, so each side accepts only its own kind of caller.</summary>
public static class ApiPolicies
{
    public const string AnyKey = "Api.AnyKey";

    public static string For(Permission permission) => $"Api.{permission}";

    public static void Add(AuthorizationOptions options)
    {
        options.AddPolicy(AnyKey, policy => policy
            .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser());

        foreach (var permission in Enum.GetValues<Permission>())
        {
            options.AddPolicy(For(permission), policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim(UserAccessClaimsTransformation.PermissionClaimType, permission.ToString()));
        }
    }
}
