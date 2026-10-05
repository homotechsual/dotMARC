using DotMarc.Data;

namespace DotMarc.Api;

/// <summary>Which permission an endpoint needs, for the OpenAPI document.</summary>
public sealed record ApiPermissionMetadata(Permission Permission);

public static class ApiEndpointConventions
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, Permission permission) =>
        builder.RequireAuthorization(ApiPolicies.For(permission)).WithMetadata(new ApiPermissionMetadata(permission));
}
