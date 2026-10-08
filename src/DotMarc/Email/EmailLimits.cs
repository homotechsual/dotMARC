namespace DotMarc.Email;

/// <summary>Checked before any provider is contacted, so an oversized send fails clearly rather than half-way.</summary>
public static class EmailLimits
{
    public const int MaximumRecipients = 25;

    /// <summary>Graph's limit for an attachment sent inline with the message. A report is typically well under 1 MB.</summary>
    public const int MaximumAttachmentBytes = 3 * 1024 * 1024;

    public static void Check(EmailMessage message)
    {
        if (message.To.Count == 0)
        {
            throw new EmailSendException("There's no one to send this to.");
        }

        if (message.To.Count > MaximumRecipients)
        {
            throw new EmailSendException($"A report can go to at most {MaximumRecipients} recipients.");
        }

        if (message.Attachments.Sum(attachment => (long)attachment.Bytes.Length) > MaximumAttachmentBytes)
        {
            throw new EmailSendException("The attachment is over 3 MB, too large to send.");
        }
    }
}
