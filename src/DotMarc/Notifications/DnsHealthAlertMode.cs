namespace DotMarc.Notifications;

/// <summary>When a DNS health check raises an alert. WhenItBreaks is first, so it's the default for a new column.</summary>
public enum DnsHealthAlertMode
{
    /// <summary>Only once the check has passed at least once, so domains that were never set up stay quiet.</summary>
    WhenItBreaks,
    WheneverItFails,
    Off
}
