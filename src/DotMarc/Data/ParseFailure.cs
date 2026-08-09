namespace DotMarc.Data;

/// <summary>Recorded whenever an inbox message could not be turned into a Report — corrupt
/// attachment, unexpected format, or not a DMARC report at all. The corresponding mailbox message
/// is deliberately left unread (see PollingService) so a fixed parser retries it automatically.</summary>
public sealed class ParseFailure
{
    public int Id { get; set; }
    public required string GraphMessageId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }
}
