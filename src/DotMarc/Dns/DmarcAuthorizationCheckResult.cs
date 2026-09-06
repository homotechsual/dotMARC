using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one DmarcDnsChecker.CheckAuthorizationAsync call. Detail is null exactly
/// when Status is Ok or NotApplicable - there's nothing to explain about a passing or
/// not-required check.</summary>
public sealed record DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus Status, string? Detail);
