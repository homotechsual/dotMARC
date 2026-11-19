namespace DotMarc.Notifications;

/// <summary>Words about a severity that more than one channel uses.</summary>
public static class AlertSeverityText
{
    /// <summary>The title a chat channel shows: a Critical alert says so up front, so it stands out in a busy channel. Other
    /// severities keep the plain title.</summary>
    public static string Title(string severity, string title) =>
        severity == AlertSeverities.Critical ? $"Critical: {title}" : title;
}
