namespace DotMarc.Notifications;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    public bool Enabled { get; set; } = true;
    public string DeliveryMode { get; set; } = "Teams";
    public string? TeamsWebhookUrl { get; set; }
    public string? GenericWebhookUrl { get; set; }
    public int MissingReportThresholdDays { get; set; } = 2;
    public int CooldownMinutes { get; set; } = 180;
    public int MonitorIntervalSeconds { get; set; } = 300;
}
