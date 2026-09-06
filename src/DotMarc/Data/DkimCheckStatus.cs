namespace DotMarc.Data;

/// <summary>The result of the most recent DKIM DNS check for a Domain — see
/// DotMarc.Dns.DkimDnsChecker. Opt-in: dotMARC has no way to discover a domain's DKIM selector(s)
/// on its own, so this only ever runs once an admin configures at least one selector
/// (Domain.DkimSelectors). NotConfigured is listed first so it is the enum's (and the database
/// column's) default value — every existing domain starts here, which is a neutral state, not a
/// failure.</summary>
public enum DkimCheckStatus
{
    NotConfigured,
    Ok,
    Missing,
    Misconfigured
}
