namespace DotMarc.Notifications;

public sealed class AlertEvent
{
    public int Id { get; set; }
    public required string DomainName { get; set; }
    public required string AlertType { get; set; }
    public required string Severity { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public bool IsResolved { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedUtc { get; set; }

    /// <summary>When the notification went out (or was muted). Null while a quiet window is holding it. Alerts raised before this
    /// existed count as notified when they were created, so an upgrade never sends them again.</summary>
    public DateTimeOffset? NotifiedUtc { get; set; }

    /// <summary>When the alert was escalated to the rule's escalation destinations. Null until then, and an alert escalates once.</summary>
    public DateTimeOffset? EscalatedUtc { get; set; }
    /// <summary>The rule that raised this alert. Null only for an alert raised before rules existed and not yet adopted by its default rule.</summary>
    public int? AlertRuleId { get; set; }
    public List<DotMarc.Psa.AlertTicket> Tickets { get; set; } = [];
}
