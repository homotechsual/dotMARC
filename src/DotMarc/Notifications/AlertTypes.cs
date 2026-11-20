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
    public const string ApiKeyExpiring = "ApiKeyExpiring";
    public const string ClientReportFailed = "ClientReportFailed";
    public const string PsaConnectionLost = "PsaConnectionLost";
    public const string NewUnreviewedSender = "NewUnreviewedSender";
    public const string UnauthorisedSenderDelivered = "UnauthorisedSenderDelivered";
    public const string PassRateLow = "PassRateLow";
    public const string VolumeChanged = "VolumeChanged";
    public const string RejectedMessagesHigh = "RejectedMessagesHigh";

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
        new(ApiKeyExpiring, "API key expiring", "An API key expires within 14 days. Make a replacement before whatever uses it stops working.", CreatesTicketByDefault: false),
        new(ClientReportFailed, "Client report failed", "A scheduled client report couldn't be sent for 24 hours.", CreatesTicketByDefault: false),
        new(PsaConnectionLost, "PSA connection lost", "dotMARC can no longer sign in to a PSA, so it isn't raising tickets there until someone reconnects.", CreatesTicketByDefault: false),
        new(NewUnreviewedSender, "New sender to review", "A sender that nobody has decided on has started sending as a domain. Mark it Authorised, Unauthorised or Under review.", CreatesTicketByDefault: false),
        new(UnauthorisedSenderDelivered, "Unauthorised sender mail delivered", "Mail from a sender marked Unauthorised is reaching inboxes. Your DMARC policy isn't blocking it."),
        new(PassRateLow, "DMARC pass rate low", "The share of a domain's mail passing DMARC over the last few days is below the level the rule sets.", CreatesTicketByDefault: false),
        new(VolumeChanged, "Message volume changed", "A domain's mail volume has dropped or risen sharply compared with the period before.", CreatesTicketByDefault: false),
        new(RejectedMessagesHigh, "Rejected or quarantined messages high", "A domain had more rejected or quarantined messages over the last few days than the rule allows.", CreatesTicketByDefault: false),
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
