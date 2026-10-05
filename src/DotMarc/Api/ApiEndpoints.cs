namespace DotMarc.Api;

public static class ApiEndpoints
{
    public const string RateLimiterPolicy = "api";

    public static RouteGroupBuilder MapDotMarcApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(ApiPolicies.AnyKey)
            .RequireRateLimiting(RateLimiterPolicy);

        GroupAndTagEndpoints.Map(api);

        // Without this, a path under /api that isn't an endpoint falls to the UI's fallback policy and redirects to
        // sign-in.
        app.MapFallback("/api/{**path}", () => ApiProblems.NotFound("such API endpoint"))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return api;
    }
}
