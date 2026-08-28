namespace DotMarc.Data;

/// <summary>The outcome of the most recent RDAP lookup for an IpInfo row. Ok is cached
/// indefinitely; NotFound/LookupFailed are retried after IpInfoService.FailureRetryWindow, in
/// case the miss was transient — see docs/superpowers/specs/2026-08-28-source-ip-enrichment-design.md.</summary>
public enum IpLookupStatus
{
    Ok,
    NotFound,
    LookupFailed
}
