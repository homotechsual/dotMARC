using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DotMarc.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DotMarc.Api;

public static class ApiServices
{
    public static IServiceCollection AddDotMarcApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ApiOptions>(configuration.GetSection(ApiOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddProblemDetails();
        // Lets UseDotMarcBadRequests say which field of a request couldn't be read.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (rejection, cancellationToken) =>
            {
                if (rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    rejection.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await ApiProblems.WriteAsync(rejection.HttpContext, StatusCodes.Status429TooManyRequests, "Too many requests",
                    "This API key has made too many requests this minute. Wait for the time in Retry-After, then try again.");
            };

            // Runs after authorization, which has already replaced the user with the key's principal.
            options.AddPolicy(ApiEndpoints.RateLimiterPolicy, httpContext =>
            {
                var keyId = httpContext.User.FindFirst(ApiKeyClaims.IdClaimType)?.Value ?? "none";
                var requestsPerMinute = httpContext.RequestServices.GetRequiredService<IOptions<ApiOptions>>().Value.RequestsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(keyId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = requestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });

        return services;
    }
}
