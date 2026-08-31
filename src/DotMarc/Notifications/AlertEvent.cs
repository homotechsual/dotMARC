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
}
