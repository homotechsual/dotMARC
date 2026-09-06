using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one MxDnsChecker.CheckAsync call. Detail is null exactly when Status is
/// Ok and it's a normal (non-null-MX) result — there's nothing to explain about a passing check.
/// An explicit null MX is Ok but still carries an explanatory Detail (see MxDnsChecker), since
/// "no mail servers" reads as suspicious without the RFC 7505 context.</summary>
public sealed record MxCheckResult(MxCheckStatus Status, string? Detail);
