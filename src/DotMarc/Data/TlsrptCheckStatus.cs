namespace DotMarc.Data;

/// <summary>The result of the most recent SMTP TLS Reporting DNS check for a Domain.</summary>
public enum TlsrptCheckStatus
{
    NotChecked,
    Ok,
    MissingOwnRecord,
    Misconfigured
}