namespace DotMarc.Graph;

public interface IGraphMailboxClient
{
    Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken);
    Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>Reads the mailbox's Inbox unread count without changing anything, to check dotMARC can reach it.</summary>
    Task<MailboxAccess> CheckAccessAsync(CancellationToken cancellationToken);
}

/// <summary>What a read-only check of a mailbox found. CanMarkRead is null when the access token couldn't be read.</summary>
public sealed record MailboxAccess(string MailboxAddress, int UnreadCount, bool? CanMarkRead);
