using Microsoft.AspNetCore.Http.HttpResults;

namespace DotMarc.Api;

/// <summary>The API's error responses, all RFC 7807 problem+json.</summary>
public static class ApiProblems
{
    public const string ScopedKeyCantAdd =
        "A key limited to certain groups can't add domains, because a new domain isn't in any group yet. Use a key that isn't limited to groups.";

    public static ProblemHttpResult NotFound(string what) =>
        TypedResults.Problem($"There's no {what}, or this key can't see it.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden, title: "Not allowed");

    public static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

    public static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static Task WriteAsync(HttpContext httpContext, int statusCode, string title, string detail) =>
        TypedResults.Problem(detail, statusCode: statusCode, title: title).ExecuteAsync(httpContext);
}
