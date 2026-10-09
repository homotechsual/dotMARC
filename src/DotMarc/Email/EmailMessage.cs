namespace DotMarc.Email;

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string TextBody, IReadOnlyList<EmailAttachment> Attachments);

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Bytes);

/// <summary>A send that failed, carrying a message fit to show staff (the provider's own words where it gave any).</summary>
public sealed class EmailSendException(string message, Exception? inner = null) : Exception(message, inner);
