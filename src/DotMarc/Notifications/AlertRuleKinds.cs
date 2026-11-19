namespace DotMarc.Notifications;

public enum AlertRuleParameterType
{
    Integer,
    Choice
}

public sealed record AlertRuleChoice(string Value, string Label);

/// <summary>One setting of a rule kind, described once so the validator, the editor and the engine agree on it. Values are
/// stored as text. <see cref="ShowWhenKey"/> and <see cref="ShowWhenValue"/>, when set, say the editor shows this parameter only
/// while another parameter has that value.</summary>
public sealed record AlertRuleParameterDefinition(
    string Key,
    string Label,
    string Help,
    AlertRuleParameterType Type,
    string Default,
    int? Min = null,
    int? Max = null,
    IReadOnlyList<AlertRuleChoice>? Choices = null,
    string? ShowWhenKey = null,
    string? ShowWhenValue = null);

/// <summary>One kind of rule, which is one kind of alert. <see cref="HasDomainScope"/> is false for the alerts that are about
/// the system (an API key, a client report, a PSA connection) rather than a domain, so their rules have no scope.
/// <see cref="HasAutoClose"/> is true for the alerts that wait to be acknowledged, which a rule can close after some days.</summary>
public sealed record AlertRuleKind(
    string Key,
    string DisplayName,
    bool HasDomainScope,
    string DefaultSeverity,
    bool HasAutoClose,
    IReadOnlyList<AlertRuleParameterDefinition> Parameters);

public static class AlertRuleKinds
{
    private static readonly HashSet<string> SystemKinds =
    [
        AlertTypes.ApiKeyExpiring, AlertTypes.ClientReportFailed, AlertTypes.PsaConnectionLost
    ];

    private static readonly HashSet<string> DnsRecordKinds =
    [
        AlertTypes.DmarcRecordBroken, AlertTypes.DmarcAuthorizationBroken, AlertTypes.TlsrptRecordBroken, AlertTypes.SpfRecordBroken,
        AlertTypes.MxRecordBroken, AlertTypes.DkimRecordBroken, AlertTypes.MtaStsFailing
    ];

    private static readonly IReadOnlyList<AlertRuleChoice> ModeChoices =
    [
        new("WhenItBreaks", "When it breaks"),
        new("WheneverItFails", "Whenever it fails")
    ];

    private static readonly IReadOnlyList<AlertRuleChoice> UnderReviewChoices =
    [
        new("decided", "Counts as decided"),
        new("unreviewed", "Counts as unreviewed"),
        new("afterDays", "Counts as decided for a while, then alert again")
    ];

    /// <summary>One per alert type, in the same order, so a new alert type is a rule kind with no further work.</summary>
    public static IReadOnlyList<AlertRuleKind> All { get; } = AlertTypes.All
        .Select(type => new AlertRuleKind(
            type.Key,
            type.DisplayName,
            HasDomainScope: !SystemKinds.Contains(type.Key),
            DefaultSeverity: type.Key == AlertTypes.NameserversChanged ? AlertSeverities.Info : AlertSeverities.Warning,
            HasAutoClose: type.Key is AlertTypes.DmarcPolicyWeakened or AlertTypes.NameserversChanged,
            Parameters: ParametersFor(type.Key)))
        .ToList();

    public static AlertRuleKind? Find(string key) => All.FirstOrDefault(kind => kind.Key == key);

    /// <summary>The default of a kind's parameter, as text. Throws for a kind or key that does not exist, so a typo fails loudly.</summary>
    public static string DefaultOf(string kind, string parameterKey) =>
        Find(kind)?.Parameters.FirstOrDefault(parameter => parameter.Key == parameterKey)?.Default
        ?? throw new ArgumentException($"Alert kind '{kind}' has no parameter '{parameterKey}'.", nameof(parameterKey));

    public static int DefaultInt(string kind, string parameterKey) =>
        int.Parse(DefaultOf(kind, parameterKey), System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyList<AlertRuleParameterDefinition> ParametersFor(string kind)
    {
        if (DnsRecordKinds.Contains(kind))
        {
            return
            [
                new(AlertRuleParameterKeys.Mode, "Alert when",
                    "When it breaks alerts only once the check has passed at least once, so a domain that was never set up stays quiet. Whenever it fails alerts for any failing check.",
                    AlertRuleParameterType.Choice, "WhenItBreaks", Choices: ModeChoices)
            ];
        }

        return kind switch
        {
            AlertTypes.MissedReport =>
            [
                new(AlertRuleParameterKeys.MissingDays, "Days without a report",
                    "How many days a monitored domain can go without a DMARC aggregate report before this alert. Most receivers report daily, so 2 allows for one late day.",
                    AlertRuleParameterType.Integer, "2", Min: 1, Max: 90)
            ],
            AlertTypes.SuspiciousRejectActivity =>
            [
                new(AlertRuleParameterKeys.MinimumMessages, "Minimum messages",
                    "Rejected or quarantined messages in the last 30 days a domain needs before this alert.",
                    AlertRuleParameterType.Integer, "10", Min: 1, Max: 1000000),
                new(AlertRuleParameterKeys.NonBenignPercent, "Minimum percent without a benign reason",
                    "How much of that mail must have no forwarder, mailing list or sampling reason before this alert.",
                    AlertRuleParameterType.Integer, "50", Min: 1, Max: 100)
            ],
            AlertTypes.NewUnreviewedSender =>
            [
                new(AlertRuleParameterKeys.NewDays, "Days a sender counts as new",
                    "A sender is new only if it first appeared on the domain within this many days, so a domain's existing senders never alert.",
                    AlertRuleParameterType.Integer, "7", Min: 1, Max: 30),
                new(AlertRuleParameterKeys.MinimumMessages, "Minimum messages",
                    "Messages a new sender needs in the last 30 days before this alert.",
                    AlertRuleParameterType.Integer, "20", Min: 1, Max: 1000000),
                new(AlertRuleParameterKeys.UnderReview, "Under review counts as",
                    "What a sender marked Under review means for this alert. Counts as decided stops the alert, as Authorised and Unauthorised do.",
                    AlertRuleParameterType.Choice, "decided", Choices: UnderReviewChoices),
                new(AlertRuleParameterKeys.UnderReviewDays, "Alert again after (days)",
                    "How long a sender can stay Under review before this alert is raised for it again.",
                    AlertRuleParameterType.Integer, "14", Min: 1, Max: 365,
                    ShowWhenKey: AlertRuleParameterKeys.UnderReview, ShowWhenValue: "afterDays")
            ],
            AlertTypes.UnauthorisedSenderDelivered =>
            [
                new(AlertRuleParameterKeys.MinimumMessages, "Minimum messages",
                    "Delivered messages from a sender marked Unauthorised, in the last 30 days, before this alert.",
                    AlertRuleParameterType.Integer, "10", Min: 1, Max: 1000000)
            ],
            _ => []
        };
    }
}
