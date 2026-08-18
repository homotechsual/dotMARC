using DotMarc.Graph;

namespace DotMarc.Tests.Internal;

internal sealed class FakeGraphMailboxClient : IGraphMailboxClient
{
    public List<MailboxMessage> UnreadMessages { get; } = [];
    public Dictionary<string, List<MailboxAttachment>> Attachments { get; } = [];
    public List<string> MarkedAsRead { get; } = [];
    public HashSet<string> FailMarkAsReadFor { get; } = [];

    public Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MailboxMessage>>(UnreadMessages);

    public Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MailboxAttachment>>(Attachments.GetValueOrDefault(messageId, []));

    public Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken)
    {
        if (FailMarkAsReadFor.Contains(messageId))
        {
            throw new HttpRequestException("Simulated transient Graph failure marking message read.");
        }

        MarkedAsRead.Add(messageId);
        return Task.CompletedTask;
    }
}
