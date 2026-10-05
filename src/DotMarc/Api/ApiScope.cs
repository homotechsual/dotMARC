using System.Globalization;
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Security;

namespace DotMarc.Api;

/// <summary>Which groups a key is limited to. No scoped-group claims means unrestricted, as for a person. A scoped key
/// sees a domain when the domain is in at least one of its groups, as DomainDetail does for a scoped person.</summary>
public sealed class ApiScope
{
    private readonly int[]? _groupIds;

    private ApiScope(int[]? groupIds) => _groupIds = groupIds;

    public bool IsScoped => _groupIds is not null;

    public static ApiScope From(ClaimsPrincipal principal)
    {
        var groupIds = principal.FindAll(UserAccessClaimsTransformation.ScopedGroupClaimType)
            .Select(claim => int.Parse(claim.Value, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
        return new ApiScope(groupIds.Length == 0 ? null : groupIds);
    }

    public bool Includes(int groupId) => _groupIds is null || _groupIds.Contains(groupId);

    public IQueryable<Domain> Domains(IQueryable<Domain> domains)
    {
        var groupIds = _groupIds;
        return groupIds is null ? domains : domains.Where(domain => domain.Groups.Any(group => groupIds.Contains(group.Id)));
    }

    public IQueryable<Group> Groups(IQueryable<Group> groups)
    {
        var groupIds = _groupIds;
        return groupIds is null ? groups : groups.Where(group => groupIds.Contains(group.Id));
    }
}
