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
