using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one SpfDnsChecker.CheckAsync call. Detail is null exactly when Status is
/// Ok - there's nothing to explain about a passing check. Ranges are the IP ranges the record authorises: null when they should be
/// left as they were (the check couldn't read them), an empty list when the domain authorises nothing.</summary>
public sealed record SpfCheckResult(SpfCheckStatus Status, string? Detail, IReadOnlyList<SpfServiceRange>? Ranges = null);
