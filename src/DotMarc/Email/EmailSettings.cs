namespace DotMarc.Email;

public enum EmailProvider { Off, Graph, Smtp }

public enum SmtpSecurity { StartTls, SslOnConnect, None }

/// <summary>Singleton row (seeded <c>Id = 1</c>) saying how dotMARC sends email. Graph sends as
/// <see cref="FromAddress"/>, which must be a mailbox in the tenant; SMTP uses the server below. The SMTP password
/// lives in ISecretStore under <see cref="SmtpPasswordSecretKey"/>, never on this entity.</summary>
public sealed class EmailSettings
{
    public const string SmtpPasswordSecretKey = "Email.SmtpPassword";

    public int Id { get; set; }
    public EmailProvider Provider { get; set; }
    public string? FromAddress { get; set; }

    /// <summary>The sender's display name for SMTP. When empty, the MSP brand's product name is used.</summary>
    public string? FromName { get; set; }

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity SmtpSecurity { get; set; }
    public string? SmtpUsername { get; set; }
    public bool SmtpPasswordConfigured { get; set; }
}
