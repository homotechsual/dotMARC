namespace DotMarc.Data;

/// <summary>What the DNS health alerts remember about one watched item of one domain between alert-monitor cycles:
/// whether a check has ever passed, the accepted DMARC policy or nameservers, and a failure or change waiting for its
/// confirmation recheck. See docs/superpowers/specs/2026-10-02-dns-health-alerts-design.md.</summary>
public sealed class DomainAlertState
{
    public int Id { get; set; }
    public int DomainId { get; set; }

    /// <summary>One of DnsHealthItems. Stored, so the values never change.</summary>
    public required string Item { get; set; }

    public bool HasPassed { get; set; }

    /// <summary>The accepted DMARC policy ("p=reject; sp=reject; pct=100") or nameservers (sorted, ";"-joined). Null
    /// for the seven checks.</summary>
    public string? Baseline { get; set; }

    public DateTimeOffset? PendingSinceUtc { get; set; }
    public DateTimeOffset? RecheckDueUtc { get; set; }
}
