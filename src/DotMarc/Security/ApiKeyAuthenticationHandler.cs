using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DotMarc.Api;
using DotMarc.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotMarc.Security;

/// <summary>Authenticates "Authorization: Bearer dmk_..." for the public API, giving the key the same permission and
/// scoped-group claims a person with its role and groups would get. Challenges and denials are problem+json, never a
/// redirect. Only the /api/v1 policies name this scheme, so a browser's cookie never reaches the API and a key never
/// reaches the UI.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    private const string BearerPrefix = "Bearer ";
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var secret = header[BearerPrefix.Length..].Trim();
        if (!secret.StartsWith(ApiKeySecrets.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeySecrets.Hash(secret);
        await using var context = await dbFactory.CreateDbContextAsync(Context.RequestAborted).ConfigureAwait(false);
        var apiKey = await context.ApiKeys
            .AsNoTracking()
            .Include(key => key.Role)
            .Include(key => key.ScopedGroups)
            .SingleOrDefaultAsync(key => key.Hash == hash, Context.RequestAborted)
            .ConfigureAwait(false);
        var nowUtc = timeProvider.GetUtcNow();
        if (apiKey?.Role is null || !apiKey.IsActive(nowUtc))
        {
            return AuthenticateResult.Fail("The API key is unknown, expired or revoked.");
        }

        // At most one write a minute per key, rather than one per request.
        if (apiKey.LastUsedUtc is null || nowUtc - apiKey.LastUsedUtc >= LastUsedPrecision)
        {
            await context.ApiKeys
                .Where(key => key.Id == apiKey.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(key => key.LastUsedUtc, nowUtc), Context.RequestAborted)
                .ConfigureAwait(false);
        }

        var identity = new ClaimsIdentity(SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, apiKey.Name));
        identity.AddClaim(new Claim(ApiKeyClaims.IdClaimType, apiKey.Id.ToString(CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(ApiKeyClaims.CreatedByClaimType, apiKey.CreatedBy));
        identity.AddClaims(AccessClaims.For(apiKey.Role, apiKey.ScopedGroups.Select(group => group.Id), forApiKey: true));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiProblems.WriteAsync(Context, StatusCodes.Status401Unauthorized, "API key required",
            "Send a valid, unexpired API key as 'Authorization: Bearer dmk_...'. Keys are created on dotMARC's Access page.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.WriteAsync(Context, StatusCodes.Status403Forbidden, "Not allowed", "This API key's role doesn't include the permission this needs.");
}
