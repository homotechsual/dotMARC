using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one DkimDnsChecker.CheckAsync call. Detail is null exactly when Status
/// is Ok - there's nothing to explain about a passing check.</summary>
public sealed record DkimCheckResult(DkimCheckStatus Status, string? Detail);
