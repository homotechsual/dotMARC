namespace DotMarc.Notifications;

/// <summary>One kind of alert dotMARC raises. <see cref="Key"/> is what is stored on an AlertEvent and in
/// ticket rules, so it must never change once shipped.</summary>
public sealed record AlertTypeInfo(string Key, string DisplayName, string Description, bool CreatesTicketByDefault = true);

/// <summary>Every alert type dotMARC raises. The ticket rule screens list this, so a new alert type added here
/// appears in them automatically, creating tickets until someone turns that off.</summary>
public static class AlertTypes
{
    public const string MissedReport = "MissedReport";
    public const string SuspiciousRejectActivity = "SuspiciousRejectActivity";
    public const string TlsrptFailure = "TlsrptFailure";
    public const string UnexpectedActivityOnNullRoutedDomain = "UnexpectedActivityOnNullRoutedDomain";

    public static IReadOnlyList<AlertTypeInfo> All { get; } =
    [
        new(MissedReport, "Missing DMARC report", "No aggregate report has arrived from a monitored domain for longer than the threshold."),
        new(SuspiciousRejectActivity, "Suspicious reject activity", "Rejected or quarantined mail looks like more than benign forwarding."),
        new(TlsrptFailure, "TLS delivery failures", "A TLSRPT report says other servers failed to deliver mail to the domain over TLS."),
        new(UnexpectedActivityOnNullRoutedDomain, "Mail activity on a null-routed domain", "A domain that should send no mail is appearing in DMARC reports."),
    ];

    public static AlertTypeInfo? Find(string key) => All.FirstOrDefault(alertType => alertType.Key == key);
}
