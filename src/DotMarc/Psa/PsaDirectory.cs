using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa;

public sealed record PsaCompanyList(PsaKind Psa, IReadOnlyList<PsaCompany>? Companies, string? FailureReason);

/// <summary>Loads the company list of every ready PSA for the pages that link Groups and Domains to companies. A PSA
/// that fails to load is reported by name and the others still load. Loading also refreshes the names stored on links,
/// which is how links copied from the old Halo columns get their real names.</summary>
public sealed class PsaDirectory(IEnumerable<IPsaProvider> providers, ILogger<PsaDirectory> logger)
{
    public async Task<IReadOnlyList<PsaCompanyList>> LoadAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var lists = new List<PsaCompanyList>();
        foreach (var provider in providers.OrderBy(candidate => candidate.Kind))
        {
            if (!(await provider.GetReadinessAsync(context, cancellationToken).ConfigureAwait(false)).IsReady)
            {
                continue;
            }

            try
            {
                var companies = await provider.ListCompaniesAsync(context, cancellationToken).ConfigureAwait(false);
                lists.Add(new PsaCompanyList(provider.Kind, companies, null));
                await RefreshNamesAsync(context, provider.Kind, companies, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Loading {Psa} companies failed", provider.Kind.DisplayName());
                lists.Add(new PsaCompanyList(provider.Kind, null, $"{provider.Kind.DisplayName()} companies couldn't be loaded: {exception.Message}"));
            }
        }

        return lists;
    }

    private static async Task RefreshNamesAsync(DotMarcDbContext context, PsaKind psa, IReadOnlyList<PsaCompany> companies, CancellationToken cancellationToken)
    {
        var namesById = companies.GroupBy(company => company.Id).ToDictionary(sameId => sameId.Key, sameId => sameId.First().Name);
        var links = await context.PsaCompanyLinks.Where(link => link.Psa == psa).ToListAsync(cancellationToken).ConfigureAwait(false);
        var renamed = false;
        foreach (var link in links)
        {
            if (namesById.TryGetValue(link.CompanyId, out var name) && name != link.CompanyName)
            {
                link.CompanyName = name;
                renamed = true;
            }
        }

        if (renamed)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
