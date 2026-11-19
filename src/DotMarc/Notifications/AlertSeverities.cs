namespace DotMarc.Notifications;

/// <summary>The severities an alert can have, from least to most serious. Stored as these exact words on a rule and on the alerts
/// it raises.</summary>
public static class AlertSeverities
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Critical = "Critical";

    public static IReadOnlyList<string> All { get; } = [Info, Warning, Critical];
}
