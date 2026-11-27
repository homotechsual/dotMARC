using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.Tests.Internal;

/// <summary>Grants only the named policies, so a test can see which policy a decision depends on.</summary>
internal sealed class PolicyNamedAuthorizationService(params string[] grantedPolicies) : IAuthorizationService
{
    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
        throw new NotSupportedException();

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
        Task.FromResult(grantedPolicies.Contains(policyName) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
}
