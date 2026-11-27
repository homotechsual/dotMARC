using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Portal;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

public static class DnsPushBatchAccess
{
    public static async Task<bool> InScopeAsync(DotMarcDbContext context, ClaimsPrincipal user, IEnumerable<int> domainIds, CancellationToken cancellationToken = default)
    {
        if (ClientPortalGate.IsPortalUser(user)) return false;
        var scope = PortalData.ScopedGroupIds(user);
        if (scope.Count == 0) return true;
        var ids = domainIds.Distinct().ToArray();
        return await context.Domains.CountAsync(domain => ids.Contains(domain.Id)
            && (scope.Count == 0 || domain.Groups.Any(group => scope.Contains(group.Id))), cancellationToken).ConfigureAwait(false) == ids.Length;
    }
}
