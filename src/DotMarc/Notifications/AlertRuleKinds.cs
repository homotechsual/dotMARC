namespace DotMarc.Notifications;

/// <summary>One kind of rule, which is one kind of alert. <see cref="HasDomainScope"/> is false for the alerts that are about
/// the system (an API key, a client report, a PSA connection) rather than a domain, so their rules have no scope.</summary>
public sealed record AlertRuleKind(string Key, string DisplayName, bool HasDomainScope, string DefaultSeverity);

public static class AlertRuleKinds
{
    private static readonly HashSet<string> SystemKinds =
    [
        AlertTypes.ApiKeyExpiring, AlertTypes.ClientReportFailed, AlertTypes.PsaConnectionLost
    ];

    /// <summary>One per alert type, in the same order, so a new alert type is a rule kind with no further work.</summary>
    public static IReadOnlyList<AlertRuleKind> All { get; } = AlertTypes.All
        .Select(type => new AlertRuleKind(
            type.Key,
            type.DisplayName,
            HasDomainScope: !SystemKinds.Contains(type.Key),
            DefaultSeverity: type.Key == AlertTypes.NameserversChanged ? "Info" : "Warning"))
        .ToList();

    public static AlertRuleKind? Find(string key) => All.FirstOrDefault(kind => kind.Key == key);

    /// <summary>Whether the global settings leave this kind on. Until the settings fields are retired (Phase 2) they still
    /// switch a kind off for every rule of it.</summary>
    public static bool IsEnabledBySettings(string kind, NotificationSettings settings) => kind switch
    {
        AlertTypes.NewUnreviewedSender => settings.NewSenderAlertEnabled,
        AlertTypes.UnauthorisedSenderDelivered => settings.UnauthorisedDeliveredAlertEnabled,
        AlertTypes.DmarcPolicyWeakened => settings.DmarcPolicyWeakenedEnabled,
        AlertTypes.NameserversChanged => settings.NameserversChangedEnabled,
        _ => DnsHealthChecks.All.FirstOrDefault(check => check.AlertType == kind) is { } check
            ? check.Mode(settings) != DnsHealthAlertMode.Off
            : true
    };
}
