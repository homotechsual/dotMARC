namespace DotMarc.Notifications;

/// <summary>Singleton settings row - exactly one is guaranteed to exist via a migration seed
/// (DotMarcDbContext.OnModelCreating's HasData for Id 1), so every reader can use SingleAsync
/// without a null/missing-row branch. Admin-editable via AlertsSettings.razor, replacing the
/// original appsettings.json-backed NotificationOptions: a file rewrite doesn't survive an Azure
/// App Service restart/redeploy and doesn't propagate across replicas, while a DB row does
/// both.</summary>
public sealed class NotificationSettings
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    public string DeliveryMode { get; set; } = "Teams";
    public string? TeamsWebhookUrl { get; set; }
    public string? GenericWebhookUrl { get; set; }
    public int MissingReportThresholdDays { get; set; } = 2;
    public int CooldownMinutes { get; set; } = 180;
    public int MonitorIntervalSeconds { get; set; } = 300;
    public int SuspiciousRejectMinVolume { get; set; } = 10;
    public int SuspiciousRejectNonBenignPercent { get; set; } = 50;
    public DnsHealthAlertMode DmarcAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode DmarcAuthorizationAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode TlsrptAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode SpfAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode MxAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode DkimAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode MtaStsAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public bool DmarcPolicyWeakenedEnabled { get; set; } = true;
    public bool NameserversChangedEnabled { get; set; } = true;

    /// <summary>Close an open DMARC policy weakened or nameservers changed alert this many days after it was raised,
    /// as if acknowledged. 0 means never.</summary>
    public int AcknowledgeableAutoCloseDays { get; set; }
}
