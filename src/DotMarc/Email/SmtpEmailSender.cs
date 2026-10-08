using MailKit.Security;
using MimeKit;

namespace DotMarc.Email;

public sealed record SmtpConnection(string Host, int Port, SmtpSecurity Security, string? Username, string? Password, string FromAddress, string FromName);

/// <summary>Sends through an SMTP server with MailKit. All recipients go in To, in one message.</summary>
public sealed class SmtpEmailSender(SmtpConnection connection) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        EmailLimits.Check(message);

        var mime = new MimeMessage { Subject = message.Subject };
        mime.From.Add(new MailboxAddress(connection.FromName, connection.FromAddress));
        mime.To.AddRange(message.To.Select(address => MailboxAddress.Parse(address)));
        var body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody };
        foreach (var attachment in message.Attachments)
        {
            body.Attachments.Add(attachment.FileName, attachment.Bytes, ContentType.Parse(attachment.ContentType));
        }

        mime.Body = body.ToMessageBody();

        using var client = new MailKit.Net.Smtp.SmtpClient();
        try
        {
            var socketOptions = connection.Security switch
            {
                SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            };
            await client.ConnectAsync(connection.Host, connection.Port, socketOptions, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(connection.Username))
            {
                await client.AuthenticateAsync(connection.Username, connection.Password ?? "", cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not EmailSendException)
        {
            throw new EmailSendException($"The SMTP server refused the message: {exception.Message}", exception);
        }
    }
}
