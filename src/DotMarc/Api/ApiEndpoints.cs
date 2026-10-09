using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;

namespace DotMarc.Api;

public static class ApiEndpoints
{
    public const string RateLimiterPolicy = "api";

    public static RouteGroupBuilder MapDotMarcApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(ApiPolicies.AnyKey)
            .RequireRateLimiting(RateLimiterPolicy)
            // Any operation can answer these; ApiDocument describes each. Errors particular to an operation are on it.
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        GroupAndTagEndpoints.Map(api);
        DomainReadEndpoints.Map(api);
        DomainWriteEndpoints.Map(api);
        ImportEndpoints.Map(api);
        AlertEndpoints.Map(api);

        // Without this, a path under /api that isn't an endpoint falls to the UI's fallback policy and redirects to
        // sign-in. Because it accepts every method, routing never gets to answer 405 itself, so it does that here.
        app.MapFallback("/api/{**path}", NotFoundOrMethodNotAllowed)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return api;
    }

    /// <summary>Turns a request body or parameter the API couldn't read into a 400 problem naming the field, instead of an
    /// empty 400. Needs RouteHandlerOptions.ThrowOnBadRequest, set in AddDotMarcApi; other paths keep the empty 400 they
    /// always had.</summary>
    public static IApplicationBuilder UseDotMarcBadRequests(this IApplicationBuilder app) =>
        app.Use(async (httpContext, next) =>
        {
            try
            {
                await next(httpContext);
            }
            catch (BadHttpRequestException exception) when (!httpContext.Response.HasStarted)
            {
                httpContext.Response.Clear();
                httpContext.Response.StatusCode = exception.StatusCode;
                if (!httpContext.Request.Path.StartsWithSegments("/api"))
                {
                    return;
                }

                var (field, message) = exception.InnerException is JsonException jsonException
                    ? (FieldFromJsonPath(jsonException.Path), $"This isn't valid JSON for this request. {jsonException.Message}")
                    : ("request", exception.Message);
                await TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }).ExecuteAsync(httpContext);
            }
        });

    private static string FieldFromJsonPath(string? path) =>
        string.IsNullOrEmpty(path) || path == "$" ? "body" : path.TrimStart('$', '.');

    private static IResult NotFoundOrMethodNotAllowed(HttpContext httpContext, EndpointDataSource endpointDataSource)
    {
        var allowedMethods = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true
                && Matches(endpoint.RoutePattern, httpContext.Request.Path))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        if (allowedMethods.Count == 0)
        {
            return ApiProblems.NotFound("such API endpoint");
        }

        httpContext.Response.Headers.Allow = string.Join(", ", allowedMethods);
        return TypedResults.Problem($"This endpoint accepts {string.Join(", ", allowedMethods)}.", statusCode: StatusCodes.Status405MethodNotAllowed,
            title: "Method not allowed");
    }

    /// <summary>Whether the path fits the pattern, including its int constraints, so /domains/abc stays a 404.</summary>
    private static bool Matches(RoutePattern pattern, PathString path)
    {
        var values = new RouteValueDictionary();
        if (!new TemplateMatcher(new RouteTemplate(pattern), new RouteValueDictionary()).TryMatch(path, values))
        {
            return false;
        }

        return pattern.ParameterPolicies.All(parameter => parameter.Value.All(policy =>
            policy.Content != "int" || int.TryParse(values[parameter.Key]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)));
    }
}
