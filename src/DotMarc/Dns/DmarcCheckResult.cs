using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one DmarcDnsChecker.CheckAsync call. Detail is null exactly when Status
/// is Ok — there's nothing to explain about a passing check.</summary>
public sealed record DmarcCheckResult(DmarcCheckStatus Status, string? Detail);
