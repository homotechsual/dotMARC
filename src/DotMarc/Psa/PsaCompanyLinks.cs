namespace DotMarc.Psa;

public static class PsaCompanyLinks
{
    /// <summary>Points an owner's link for one PSA at a company, adding or removing the link as needed. Returns the link
    /// to delete, if any, because removing it from the navigation alone would try to null its owner.</summary>
    public static PsaCompanyLink? Apply(List<PsaCompanyLink> links, PsaCompanyLink? existing, PsaKind psa, PsaCompany? company)
    {
        if (company is null)
        {
            if (existing is not null)
            {
                links.Remove(existing);
            }

            return existing;
        }

        if (existing is null)
        {
            links.Add(new PsaCompanyLink { Psa = psa, CompanyId = company.Id, CompanyName = company.Name });
            return null;
        }

        existing.CompanyId = company.Id;
        existing.CompanyName = company.Name;
        return null;
    }
}
