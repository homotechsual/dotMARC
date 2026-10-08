namespace DotMarc.Email;

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>The sender for the saved email settings, or null when email is Off (always, in the demo).</summary>
public interface IEmailSenderFactory
{
    Task<IEmailSender?> GetAsync(CancellationToken cancellationToken);
}
