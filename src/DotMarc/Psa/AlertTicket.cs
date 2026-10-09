namespace DotMarc.Psa;

/// <summary>A ticket raised in one PSA for an alert. <see cref="IsOpen"/> stays true until dotMARC closes it or sees it
/// closed (or deleted) in the PSA, so a close that failed is retried by the poller.</summary>
public sealed class AlertTicket
{
    public int Id { get; set; }
    public int AlertEventId { get; set; }
    public PsaKind Psa { get; set; }
    public required string TicketId { get; set; }
    public bool IsOpen { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedUtc { get; set; }
}
