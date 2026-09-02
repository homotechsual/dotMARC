using DotMarc.Data;

namespace DotMarc.Dns;

public sealed record TlsrptCheckResult(TlsrptCheckStatus Status, string? Detail);