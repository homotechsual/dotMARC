using DotMarc.Data;

namespace DotMarc.Notifications;

/// <summary>Resolves which Halo client a domain's ticket should be created against. Domain and
/// Group is an implicit EF many-to-many with no order column, so "the domain's Groups" has no
/// natural order — lowest Group.Id (oldest-created) is the deterministic tie-break.</summary>
public static class HaloClientResolver
{
    public static int? Resolve(Domain domain)
    {
        if (domain.HaloClientId is { } domainOverride)
        {
            return domainOverride;
        }

        return domain.Groups
            .Where(g => g.HaloClientId is not null)
            .OrderBy(g => g.Id)
            .Select(g => g.HaloClientId)
            .FirstOrDefault();
    }
}
