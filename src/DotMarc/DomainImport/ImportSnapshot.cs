using DotMarc.Data;
using DotMarc.MtaSts;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DomainImport;

/// <summary>A domain the import names that is already monitored, as it was when the preview was built.</summary>
public sealed record ExistingDomain(
    int Id,
    string Name,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Tags,
    int? HaloClientId,
    bool IsMonitored,
    IReadOnlyList<string> DkimSelectors,
    bool MtaStsEnabled,
    MtaStsMode MtaStsMode,
    IReadOnlyList<string> MtaStsMxHosts,
    int MtaStsMaxAgeSeconds);

/// <summary>Everything the planner needs from outside the input, loaded once, so re-planning when the person changes a
/// choice is instant and needs no database.</summary>
public sealed record ImportSnapshot(
    IReadOnlyDictionary<string, ExistingDomain> DomainsByName,
    IReadOnlyList<string> GroupNames,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<HaloClient>? HaloClients,
    string? HaloUnavailableReason,
    IReadOnlyDictionary<string, IReadOnlyList<string>> LookedUpMxHosts);

public static class ImportSnapshotLoader
{
    private const int ParallelMxLookups = 8;

    public static async Task<ImportSnapshot> LoadAsync(DotMarcDbContext context, ImportTable table, IReadOnlyList<HaloClient>? haloClients,
        string? haloUnavailableReason, IMxHostsLookup mxHostsLookup, CancellationToken cancellationToken)
    {
        var validNames = table.Rows
            .Select(row => DomainNameValidator.TryNormalize(row.RawDomain, out var name) ? name : null)
            .OfType<string>()
            .Distinct()
            .ToList();

        // Domains added before names were normalised may be stored in Unicode ("bücher.example") rather than the xn--
        // form the import uses, so look for both.
        var storedForms = validNames.Concat(validNames.Select(UnicodeForm).OfType<string>()).Distinct().ToList();

        var domains = await context.Domains
            .AsNoTracking()
            .Where(domain => storedForms.Contains(domain.Name))
            .Include(domain => domain.Groups)
            .Include(domain => domain.Tags)
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Keyed by the normalised name. If both forms are stored, the xn-- one (an exact match) wins.
        var domainsByName = domains
            .GroupBy(domain => DomainNameValidator.TryNormalize(domain.Name, out var normalized) ? normalized : domain.Name)
            .ToDictionary(
                sameDomain => sameDomain.Key,
                sameDomain => ToExisting(sameDomain.FirstOrDefault(domain => domain.Name == sameDomain.Key) ?? sameDomain.First()));

        var groupNames = await context.Groups.AsNoTracking().OrderBy(group => group.Name).Select(group => group.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tagNames = await context.Tags.AsNoTracking().OrderBy(tag => tag.Name).Select(tag => tag.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

        var lookedUp = await LookUpMxHostsAsync(table, domainsByName, mxHostsLookup, cancellationToken).ConfigureAwait(false);
        return new ImportSnapshot(domainsByName, groupNames, tagNames, haloClients, haloUnavailableReason, lookedUp);
    }

    private static readonly System.Globalization.IdnMapping Idn = new();

    private static ExistingDomain ToExisting(Domain domain) =>
        new(domain.Id, domain.Name,
            domain.Groups.Select(group => group.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            domain.Tags.Select(tag => tag.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            domain.HaloClientId, domain.IsMonitored, domain.DkimSelectors, domain.MtaStsEnabled, domain.MtaStsMode,
            domain.MtaStsMxHosts, domain.MtaStsMaxAgeSeconds);

    /// <summary>The Unicode form of an xn-- name, or null if it has none.</summary>
    private static string? UnicodeForm(string asciiName)
    {
        try
        {
            var unicode = Idn.GetUnicode(asciiName);
            return unicode == asciiName ? null : unicode;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>MX hosts from DNS for the domains that turn MTA-STS on without any, as the domain page's Enable button
    /// does. A failed lookup counts as none found.</summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> LookUpMxHostsAsync(ImportTable table,
        IReadOnlyDictionary<string, ExistingDomain> domainsByName, IMxHostsLookup mxHostsLookup, CancellationToken cancellationToken)
    {
        var needed = table.Rows
            .Where(row => row.MtaStsMode is MtaStsImportMode.None or MtaStsImportMode.Testing or MtaStsImportMode.Enforce && row.MtaStsMxHosts is null)
            .Select(row => DomainNameValidator.TryNormalize(row.RawDomain, out var name) ? name : null)
            .OfType<string>()
            .Where(name => !domainsByName.TryGetValue(name, out var existing) || existing.MtaStsMxHosts.Count == 0)
            .Distinct()
            .ToList();

        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>>();
        await Parallel.ForEachAsync(needed, new ParallelOptions { MaxDegreeOfParallelism = ParallelMxLookups, CancellationToken = cancellationToken },
            async (domainName, token) =>
            {
                try
                {
                    found[domainName] = await mxHostsLookup.LookupAsync(domainName, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    found[domainName] = [];
                }
            }).ConfigureAwait(false);

        return found;
    }
}
