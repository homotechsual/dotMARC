using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa;

/// <summary>Works out, for one PSA, which company a domain's ticket goes to and which Group decides its ticket rules.
/// Domain and Group is an implicit many-to-many with no order, so the lowest Group.Id (oldest) breaks ties. Each PSA is
/// resolved on its own, so a domain can take its Halo client from one Group and its ConnectWise company from another.</summary>
public static class PsaCompanyResolver
{
    /// <summary>Loads what <see cref="Resolve"/> and <see cref="ResolveGroup"/> read.</summary>
    public static IQueryable<Domain> IncludeLinks(IQueryable<Domain> domains) =>
        domains.Include(domain => domain.PsaCompanyLinks).Include(domain => domain.Groups).ThenInclude(group => group.PsaCompanyLinks).AsSplitQuery();

    public static PsaCompanyLink? Resolve(Domain domain, PsaKind psa) =>
        domain.PsaCompanyLinks.FirstOrDefault(link => link.Psa == psa)
        ?? ResolveGroup(domain, psa)?.PsaCompanyLinks.First(link => link.Psa == psa);

    /// <summary>The Group whose company the ticket goes to, which is also the Group whose ticket rules apply. Null when
    /// the domain has its own link for this PSA (the global rules apply) or when none of its Groups is linked in it.</summary>
    public static Group? ResolveGroup(Domain domain, PsaKind psa)
    {
        if (domain.PsaCompanyLinks.Any(link => link.Psa == psa))
        {
            return null;
        }

        return domain.Groups
            .Where(group => group.PsaCompanyLinks.Any(link => link.Psa == psa))
            .OrderBy(group => group.Id)
            .FirstOrDefault();
    }
}
