namespace DotMarc.DnsPush;

/// <summary>Shared "does this push need a confirm dialog first" decision, used identically by the
/// MTA-STS, DMARC, and TLSRPT push handlers. Each caller computes its own existingValue/
/// proposedValue first (the merge logic differs per record type — DmarcRuaMerge, TlsrptRuaMerge,
/// or MTA-STS's plain hosting-hostname target); this is only the generic "should I ask first"
/// step.</summary>
public static class DnsRecordPushDecision
{
    /// <summary>True when there's something to warn about before pushing: an existing value that
    /// differs from what's about to be pushed, or a third-party CNAME delegation (which always
    /// warrants a warning regardless of value comparison, since it's a different kind of record
    /// entirely, not just a different value of the same kind).</summary>
    public static bool NeedsConfirmation(string? existingValue, string? delegatedToCname, string proposedValue) =>
        delegatedToCname is not null || (existingValue is not null && !string.Equals(existingValue, proposedValue, StringComparison.Ordinal));
}
