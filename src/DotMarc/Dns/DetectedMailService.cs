namespace DotMarc.Dns;

public enum DetectedMailServiceKind
{
    Inbox,
    Sending
}

/// <summary>One mail service detected for a domain - ProviderName is a fixed display string (e.g.
/// "Microsoft 365"), Kind distinguishes an inbox provider (matched via MX) from a sending service
/// (matched via an SPF include: mechanism). A domain can produce zero, one, or several of these -
/// see MailServiceDetector's lookup tables.</summary>
public sealed record DetectedMailService(string ProviderName, DetectedMailServiceKind Kind);
