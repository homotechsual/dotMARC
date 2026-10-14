using Microsoft.Extensions.Options;

namespace DotMarc.Graph;

public sealed record MailboxCheckResult(string Purpose, string MailboxAddress, bool CanRead, int? UnreadCount, string? Problem)
{
    public string Summary => !CanRead
        ? $"dotMARC can't read {MailboxAddress}."
        : $"dotMARC can read {MailboxAddress}. {UnreadCount} unread in the Inbox, waiting for the next poll.";
}

/// <summary>Checks, without changing anything, that dotMARC can reach each mailbox it polls: the DMARC reports mailbox,
/// and the TLS reports mailbox when one is set up. For the "Check mailbox access" button on the dashboard.</summary>
public sealed class MailboxAccessCheck(IGraphMailboxClient dmarcMailbox, Func<ITlsrptGraphMailboxClient?> tlsrptMailbox, IOptions<GraphOptions> options)
{
    public async Task<IReadOnlyList<MailboxCheckResult>> CheckAsync(CancellationToken cancellationToken)
    {
        var results = new List<MailboxCheckResult> { await CheckAsync("DMARC reports", options.Value.MailboxAddress, dmarcMailbox, cancellationToken).ConfigureAwait(false) };
        if (!string.IsNullOrWhiteSpace(options.Value.TlsrptMailboxAddress) && tlsrptMailbox() is { } tlsrpt)
        {
            results.Add(await CheckAsync("TLS reports", options.Value.TlsrptMailboxAddress, tlsrpt, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static async Task<MailboxCheckResult> CheckAsync(string purpose, string mailboxAddress, IGraphMailboxClient mailbox, CancellationToken cancellationToken)
    {
        try
        {
            var access = await mailbox.CheckAccessAsync(cancellationToken).ConfigureAwait(false);
            var problem = access.CanMarkRead == false
                ? "dotMARC can read this mailbox but can't mark reports as read, so they stay unread and are fetched again every poll. "
                    + "Give the mail app registration the Mail.ReadWrite application permission, with admin consent, in place of Mail.Read."
                : null;
            return new MailboxCheckResult(purpose, access.MailboxAddress, true, access.UnreadCount, problem);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A GraphAccessException already says what to do; anything else (a bad client secret, say) at least shows
            // Microsoft's own error, which carries the AADSTS code.
            var problem = exception is GraphAccessException ? exception.Message : $"Couldn't reach the mailbox: {exception.Message}";
            return new MailboxCheckResult(purpose, mailboxAddress, false, null, problem);
        }
    }
}
