namespace DotMarc.Graph;

public interface IGraphMailboxClient
{
    Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken);
    Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken);
}
