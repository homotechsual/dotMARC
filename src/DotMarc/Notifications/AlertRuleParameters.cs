using DotMarc.Reporting;
using System.Globalization;
using System.Text.Json;

namespace DotMarc.Notifications;

/// <summary>The names a rule's parameters are stored under.</summary>
public static class AlertRuleParameterKeys
{
    /// <summary>Missing DMARC report: days without a report.</summary>
    public const string MissingDays = "days";

    /// <summary>Suspicious reject activity, new sender and unauthorised sender delivered: messages needed before it alerts.</summary>
    public const string MinimumMessages = "minimumMessages";

    /// <summary>Suspicious reject activity: percent of reject or quarantine volume without a benign reason.</summary>
    public const string NonBenignPercent = "nonBenignPercent";

    /// <summary>New sender: days a sender counts as new.</summary>
    public const string NewDays = "newDays";

    /// <summary>The seven DNS record kinds: a <see cref="DnsHealthAlertMode"/> name.</summary>
    public const string Mode = "mode";

    /// <summary>New sender: what a sender marked Under review means for the alert: decided, unreviewed or afterDays.</summary>
    public const string UnderReview = "underReview";

    /// <summary>New sender: with <c>afterDays</c>, how many days Under review lasts before the alert is raised again.</summary>
    public const string UnderReviewDays = "underReviewDays";

    /// <summary>Pass rate low: the percentage passing DMARC that the rate must stay at or above.</summary>
    public const string BelowPercent = "belowPercent";

    /// <summary>The three metric kinds: how many days back the numbers are counted over.</summary>
    public const string WindowDays = "windowDays";

    /// <summary>Message volume changed: drop, rise or either.</summary>
    public const string Direction = "direction";

    /// <summary>Message volume changed: the percentage the volume must move by.</summary>
    public const string ChangePercent = "changePercent";

    /// <summary>Rejected or quarantined messages high: how many messages in the window raise the alert.</summary>
    public const string MessagesAtLeast = "messagesAtLeast";
}

/// <summary>A rule's own parameter values. A value the rule does not set, or sets to something unreadable, falls back to the
/// value the caller passes, which is the global setting. So a rule with no parameters behaves as the settings say.</summary>
public sealed class AlertRuleParameters
{
    private readonly Dictionary<string, string> _values;

    public AlertRuleParameters(string? json)
    {
        _values = Parse(json);
    }

    public int Int(string key, int fallback) =>
        _values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public string Text(string key, string fallback) =>
        _values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : fallback;

    /// <summary>What Under review means for a new sender rule: decided unless the rule says otherwise. Alerting again after some days
    /// uses the rule's days, or 14 when it sets none.</summary>
    public UnderReviewHandling UnderReview()
    {
        _values.TryGetValue(AlertRuleParameterKeys.UnderReview, out var mode);
        return mode switch
        {
            "unreviewed" => new UnderReviewHandling(UnderReviewMode.CountsAsUnreviewed),
            "afterDays" => new UnderReviewHandling(UnderReviewMode.AlertAgainAfterDays, Int(AlertRuleParameterKeys.UnderReviewDays, AlertRuleKinds.DefaultInt(AlertTypes.NewUnreviewedSender, AlertRuleParameterKeys.UnderReviewDays))),
            _ => UnderReviewHandling.Default
        };
    }

    public DnsHealthAlertMode Mode(DnsHealthAlertMode fallback) =>
        _values.TryGetValue(AlertRuleParameterKeys.Mode, out var text) && Enum.TryParse<DnsHealthAlertMode>(text, ignoreCase: false, out var mode) && Enum.IsDefined(mode)
            ? mode
            : fallback;

    private static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
