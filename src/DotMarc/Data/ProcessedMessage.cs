namespace DotMarc.Data;

/// <summary>Recorded for every mailbox message PollingService has successfully turned into a
/// stored Report. Checked before re-fetching and re-parsing a message's attachments, so a message
/// whose MarkAsReadAsync call keeps failing (see PollingService.PollOnceAsync) doesn't get
/// re-downloaded and re-parsed every poll cycle forever — only a cheap mark-as-read retry.</summary>
public sealed class ProcessedMessage
{
    public int Id { get; set; }
    public required string GraphMessageId { get; set; }
    public DateTimeOffset ProcessedUtc { get; set; }
}
