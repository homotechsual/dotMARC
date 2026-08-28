namespace DotMarc.Data;

/// <summary>Cached RDAP lookup result for one source IP, keyed by the IP itself rather than by
/// ReportRecord — the same IP is shared across many reports and domains, so one lookup serves
/// every future view of it anywhere in the app. Organization/Country are null when Status isn't
/// Ok, or when the RDAP response for an Ok lookup simply didn't include that field (RDAP
/// structure varies between registries — see RdapResponseParser).</summary>
public sealed class IpInfo
{
    public required string Ip { get; set; }
    public string? Organization { get; set; }
    public string? Country { get; set; }
    public IpLookupStatus Status { get; set; }
    public DateTimeOffset LookedUpUtc { get; set; }
}
