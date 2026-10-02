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
    public const string DmarcRecordBroken = "DmarcRecordBroken";
    public const string DmarcAuthorizationBroken = "DmarcAuthorizationBroken";
    public const string TlsrptRecordBroken = "TlsrptRecordBroken";
    public const string SpfRecordBroken = "SpfRecordBroken";
    public const string MxRecordBroken = "MxRecordBroken";
    public const string DkimRecordBroken = "DkimRecordBroken";
    public const string MtaStsFailing = "MtaStsFailing";
    public const string DmarcPolicyWeakened = "DmarcPolicyWeakened";
    public const string NameserversChanged = "NameserversChanged";

    public static IReadOnlyList<AlertTypeInfo> All { get; } =
    [
        new(MissedReport, "Missing DMARC report", "No aggregate report has arrived from a monitored domain for longer than the threshold."),
        new(SuspiciousRejectActivity, "Suspicious reject activity", "Rejected or quarantined mail looks like more than benign forwarding."),
        new(TlsrptFailure, "TLS delivery failures", "A TLSRPT report says other servers failed to deliver mail to the domain over TLS."),
        new(UnexpectedActivityOnNullRoutedDomain, "Mail activity on a null-routed domain", "A domain that should send no mail is appearing in DMARC reports."),
        new(DmarcRecordBroken, "DMARC record broken", "The domain's DMARC record is missing or broken."),
        new(DmarcAuthorizationBroken, "DMARC authorization record broken", "The record that lets reports for this domain go to dotMARC's mailbox is missing."),
        new(TlsrptRecordBroken, "TLS-RPT record broken", "The domain's TLS reporting record is missing or broken."),
        new(SpfRecordBroken, "SPF record broken", "The domain's SPF record is missing, duplicated or broken."),
        new(MxRecordBroken, "MX broken", "The domain has no MX record, or its mail server's name doesn't resolve."),
        new(DkimRecordBroken, "DKIM broken", "A DKIM selector set for the domain is missing or broken."),
        new(MtaStsFailing, "MTA-STS failing", "dotMARC stopped being able to serve the domain's MTA-STS policy."),
        new(DmarcPolicyWeakened, "DMARC policy weakened", "The domain's DMARC policy, subdomain policy or percentage went down."),
        new(NameserversChanged, "Nameservers changed", "The domain's nameservers changed, often the start of a DNS migration.", CreatesTicketByDefault: false),
    ];

    /// <summary>The DNS health alert types (see DnsHealthAlertEvaluator), which close themselves when the domain stops
    /// being monitored.</summary>
    public static IReadOnlyList<string> DnsHealth { get; } =
    [
        DmarcRecordBroken, DmarcAuthorizationBroken, TlsrptRecordBroken, SpfRecordBroken, MxRecordBroken, DkimRecordBroken,
        MtaStsFailing, DmarcPolicyWeakened, NameserversChanged
    ];

    public static AlertTypeInfo? Find(string key) => All.FirstOrDefault(alertType => alertType.Key == key);
}
