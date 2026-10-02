namespace DotMarc.Notifications;

/// <summary>What the DNS health alerts watch, as stored in DomainAlertState.Item. Stored, so the values never change.</summary>
public static class DnsHealthItems
{
    public const string Dmarc = "Dmarc";
    public const string DmarcAuthorization = "DmarcAuthorization";
    public const string Tlsrpt = "Tlsrpt";
    public const string Spf = "Spf";
    public const string Mx = "Mx";
    public const string Dkim = "Dkim";
    public const string MtaSts = "MtaSts";
    public const string DmarcPolicy = "DmarcPolicy";
    public const string Nameservers = "Nameservers";
}
