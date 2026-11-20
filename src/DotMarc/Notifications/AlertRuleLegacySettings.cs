using System.Globalization;

namespace DotMarc.Notifications;

/// <summary>Where each alert setting used to live on <see cref="NotificationSettings"/>, so a default rule can be created with the
/// value an install already had. Only the seeder reads these; the alerting engine reads rules.</summary>
internal static class AlertRuleLegacySettings
{
    /// <summary>The kind's whole parameter set: each definition's default, replaced by the value the settings held for it.</summary>
    public static Dictionary<string, string> ParametersFor(string kind, NotificationSettings settings)
    {
        var values = (AlertRuleKinds.Find(kind)?.Parameters ?? []).ToDictionary(parameter => parameter.Key, parameter => parameter.Default);

        switch (kind)
        {
            case AlertTypes.MissedReport:
                values[AlertRuleParameterKeys.MissingDays] = Text(settings.MissingReportThresholdDays);
                break;
            case AlertTypes.SuspiciousRejectActivity:
                values[AlertRuleParameterKeys.MinimumMessages] = Text(settings.SuspiciousRejectMinVolume);
                values[AlertRuleParameterKeys.NonBenignPercent] = Text(settings.SuspiciousRejectNonBenignPercent);
                break;
            case AlertTypes.NewUnreviewedSender:
                values[AlertRuleParameterKeys.NewDays] = Text(settings.NewSenderDays);
                values[AlertRuleParameterKeys.MinimumMessages] = Text(settings.NewSenderMinVolume);
                break;
            case AlertTypes.UnauthorisedSenderDelivered:
                values[AlertRuleParameterKeys.MinimumMessages] = Text(settings.UnauthorisedDeliveredMinVolume);
                break;
            default:
                if (DnsHealthChecks.All.FirstOrDefault(check => check.AlertType == kind) is { } check && check.Mode(settings) == DnsHealthAlertMode.WheneverItFails)
                {
                    // Off is a switched off rule (see IsEnabled), which keeps the ordinary mode for when it is switched back on.
                    values[AlertRuleParameterKeys.Mode] = nameof(DnsHealthAlertMode.WheneverItFails);
                }

                break;
        }

        return values;
    }

    /// <summary>Whether the settings left this kind on. A DNS record check whose mode was Off is off.</summary>
    public static bool IsEnabled(string kind, NotificationSettings settings) => kind switch
    {
        AlertTypes.NewUnreviewedSender => settings.NewSenderAlertEnabled,
        AlertTypes.UnauthorisedSenderDelivered => settings.UnauthorisedDeliveredAlertEnabled,
        AlertTypes.DmarcPolicyWeakened => settings.DmarcPolicyWeakenedEnabled,
        AlertTypes.NameserversChanged => settings.NameserversChangedEnabled,
        _ => DnsHealthChecks.All.FirstOrDefault(check => check.AlertType == kind) is { } check
            ? check.Mode(settings) != DnsHealthAlertMode.Off
            : true
    };

    /// <summary>The days after which a policy or nameserver alert closes itself. Null (the setting's 0) means never.</summary>
    public static int? AutoCloseDays(string kind, NotificationSettings settings) =>
        kind is AlertTypes.DmarcPolicyWeakened or AlertTypes.NameserversChanged && settings.AcknowledgeableAutoCloseDays > 0
            ? settings.AcknowledgeableAutoCloseDays
            : null;

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
