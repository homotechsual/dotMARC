using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one SpfDnsChecker.CheckAsync call. Detail is null exactly when Status is
/// Ok - there's nothing to explain about a passing check.</summary>
public sealed record SpfCheckResult(SpfCheckStatus Status, string? Detail);
