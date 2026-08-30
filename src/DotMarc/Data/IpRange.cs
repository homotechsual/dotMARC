namespace DotMarc.Data;

/// <summary>Cached RDAP lookup result for a whole registry allocation block (e.g.
/// "2a01:110::/31"), keyed by its start/end address bounds rather than by a single IP — see
/// IpInfo's own doc comment for why per-IP caching alone isn't enough: many source IPs seen in
/// practice (e.g. several of a single sender's outbound relays) fall within the same RDAP
/// allocation, so caching the range means only the first of them ever needs a live RDAP call.
/// IpRangeMatcher does the containment check against these rows. Only ever populated from an Ok
/// lookup result (see RdapResponseParser.ParseRange) — a NotFound/LookupFailed result has no
/// reliable bounds to cache, and Ok is already cached indefinitely by this app's convention (see
/// IpInfoService.FailureRetryWindow), so there's no Status/retry concept here.</summary>
public sealed class IpRange
{
    public required string RangeStart { get; set; }
    public required string RangeEnd { get; set; }
    public string? Organization { get; set; }
    public string? Country { get; set; }
    public DateTimeOffset LookedUpUtc { get; set; }
}
