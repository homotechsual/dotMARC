namespace DotMarc.Notifications;

/// <summary>Where an alert rule's alerts can be sent: a Teams or Slack channel, or a generic webhook. Replaces the three fixed
/// channels on <see cref="NotificationSettings"/>. See docs/superpowers/specs/2026-11-19-alert-destinations-design.md.</summary>
public sealed class AlertDestination
{
    public int Id { get; set; }

    /// <summary>Unique, ignoring case.</summary>
    public required string Name { get; set; }

    public AlertChannel Kind { get; set; }

    /// <summary>The webhook URL. It carries the token that lets anyone post to the channel, so treat it as a secret.</summary>
    public required string Url { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Links a rule whose <see cref="AlertRule.DestinationMode"/> is Selected to one of its destinations.</summary>
public sealed class AlertRuleDestination
{
    public int AlertRuleId { get; set; }
    public int AlertDestinationId { get; set; }
}

public enum AlertDestinationMode
{
    /// <summary>Every enabled destination.</summary>
    All,

    /// <summary>Only the destinations linked to the rule. None linked means the rule's alerts are not sent anywhere.</summary>
    Selected
}

/// <summary>Links a rule to a destination that is also told when the rule's alert stays open past its escalation time.</summary>
public sealed class AlertRuleEscalationDestination
{
    public int AlertRuleId { get; set; }
    public int AlertDestinationId { get; set; }
}

public enum AlertQuietMode
{
    Off,

    /// <summary>The alert is recorded straight away and its notification waits until the quiet window ends, if it is still open then.</summary>
    Hold,

    /// <summary>The alert is recorded and shown, but never notified.</summary>
    Mute
}

public enum AlertTicketMode
{
    /// <summary>The ticket rules decide, as they always have.</summary>
    FollowRules,
    Never,

    /// <summary>A ticket is opened whatever the ticket rules say, provided a PSA is ready and the domain has a company there.</summary>
    Always
}
