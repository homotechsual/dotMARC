namespace DotMarc.Data;

/// <summary>A monitored domain. Rows are created automatically the first time a report arrives
/// for a domain (auto-discovery); <see cref="IsMonitored"/> is set explicitly via the dashboard
/// and only affects whether a missing-report warning is shown for that domain - it does not
/// change ingestion behavior.</summary>
public sealed class Domain
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsMonitored { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset? LastReportReceivedUtc { get; set; }
    public DmarcCheckStatus DmarcCheckStatus { get; set; }
    public DateTimeOffset? DmarcCheckedUtc { get; set; }
    public string? DmarcCheckDetail { get; set; }
    // Read from the DMARC record by the DMARC check, with sp and pct filled in from their defaults; all null when
    // there's no DMARC record. The DMARC policy weakened alert compares these with the accepted policy.
    public DmarcPolicyLevel? DmarcPolicy { get; set; }
    public DmarcPolicyLevel? DmarcSubdomainPolicy { get; set; }
    public int? DmarcPercent { get; set; }

    /// <summary>The np tag the DMARC record actually publishes (strictest valid value); null when it has none.</summary>
    public DmarcPolicyLevel? DmarcNonExistentSubdomainPolicy { get; set; }

    /// <summary>True when the DMARC record has t=y (testing mode, RFC 9989).</summary>
    public bool DmarcTesting { get; set; }

    /// <summary>Every DMARC-like TXT value at _dmarc.&lt;domain&gt; at the last check: the records, then any
    /// look-alike values ignored beside them. Empty when there is no DMARC record.</summary>
    public List<string> DmarcRecords { get; set; } = [];

    /// <summary>The problems found in the DMARC record, each as its kind or kind:tag, for example RepeatedTag:p.</summary>
    public List<string> DmarcProblems { get; set; } = [];

    /// <summary>The DMARC policy this domain should reach. Null when none is set.</summary>
    public DmarcPolicyLevel? TargetDmarcPolicy { get; set; }

    /// <summary>The target subdomain policy. Null follows <see cref="TargetDmarcPolicy"/>.</summary>
    public DmarcPolicyLevel? TargetDmarcSubdomainPolicy { get; set; }
    public bool OverrideAdditionalRuaAddress { get; set; }
    public string? AdditionalRuaAddress { get; set; }

    /// <summary>The target this row holds, or null when none is set. An unset subdomain policy follows the policy.</summary>
    public DotMarc.Baselines.DmarcTarget? DmarcTarget =>
        TargetDmarcPolicy is { } policy ? new(policy, TargetDmarcSubdomainPolicy) : null;
    public TlsrptCheckStatus TlsrptCheckStatus { get; set; }
    public DateTimeOffset? TlsrptCheckedUtc { get; set; }
    public string? TlsrptCheckDetail { get; set; }

    public DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus { get; set; }
    public DateTimeOffset? DmarcAuthorizationCheckedUtc { get; set; }
    public string? DmarcAuthorizationCheckDetail { get; set; }

    public SpfCheckStatus SpfCheckStatus { get; set; }
    public DateTimeOffset? SpfCheckedUtc { get; set; }
    public string? SpfCheckDetail { get; set; }

    /// <summary>The IP ranges the domain's SPF record authorises, as JSON written by the SPF check (see <c>SpfServiceRanges</c>).</summary>
    public string? SpfRangesJson { get; set; }

    public MxCheckStatus MxCheckStatus { get; set; }
    public DateTimeOffset? MxCheckedUtc { get; set; }
    public string? MxCheckDetail { get; set; }

    public List<string> DkimSelectors { get; set; } = [];
    public DkimCheckStatus DkimCheckStatus { get; set; }
    public DateTimeOffset? DkimCheckedUtc { get; set; }
    public string? DkimCheckDetail { get; set; }

    public bool MtaStsEnabled { get; set; }
    public MtaStsStatus MtaStsStatus { get; set; }
    public DateTimeOffset? MtaStsCheckedUtc { get; set; }
    public string? MtaStsCheckDetail { get; set; }
    public MtaStsMode MtaStsMode { get; set; }
    public int MtaStsMaxAgeSeconds { get; set; } = 604_800;
    public List<string> MtaStsMxHosts { get; set; } = [];

    public DetectedDnsProvider DnsProvider { get; set; }
    public string? DnsZone { get; set; }
    public DateTimeOffset? DnsProviderCheckedUtc { get; set; }
    public List<string> DnsNameservers { get; set; } = [];

    public List<Report> Reports { get; set; } = [];
    public List<TlsrptReport> TlsrptReports { get; set; } = [];
    public List<Group> Groups { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<DomainAlertState> AlertStates { get; set; } = [];
    public List<DomainDkimRecord> DkimRecords { get; set; } = [];
    public List<DotMarc.Psa.PsaCompanyLink> PsaCompanyLinks { get; set; } = [];
}
