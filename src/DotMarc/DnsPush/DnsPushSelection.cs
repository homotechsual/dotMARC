using DotMarc.Data;

namespace DotMarc.DnsPush;

public sealed record SelectedDnsDomain(int Id, string Name, DetectedDnsProvider Provider);
public sealed record DnsPushProviderGroup(string Provider, IReadOnlyList<SelectedDnsDomain> Domains)
{
    public bool OverLimit => Domains.Count > DnsPushBatchLimits.MaxDomains;
}

public sealed record DnsPushSelection(IReadOnlyList<DnsPushProviderGroup> Providers, IReadOnlyList<SelectedDnsDomain> Manual)
{
    public static DnsPushSelection Group(IEnumerable<SelectedDnsDomain> domains, IReadOnlyCollection<string> configuredProviders)
    {
        var selected = domains.DistinctBy(domain => domain.Id).ToList();
        bool Configured(SelectedDnsDomain domain) => domain.Provider.ToProviderKey() is { } key && configuredProviders.Contains(key, StringComparer.OrdinalIgnoreCase);
        return new(selected.Where(Configured).GroupBy(domain => domain.Provider.ToProviderKey()!)
            .OrderBy(group => group.Key).Select(group => new DnsPushProviderGroup(group.Key, group.OrderBy(domain => domain.Name).ToList())).ToList(),
            selected.Where(domain => !Configured(domain)).OrderBy(domain => domain.Name).ToList());
    }
}
