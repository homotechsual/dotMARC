namespace DotMarc.Data;

/// <summary>A monitored domain. Rows are created automatically the first time a report arrives
/// for a domain (auto-discovery); <see cref="IsMonitored"/> is set explicitly via the dashboard
/// and only affects whether a missing-report warning is shown for that domain — it does not
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
    public TlsrptCheckStatus TlsrptCheckStatus { get; set; }
    public DateTimeOffset? TlsrptCheckedUtc { get; set; }
    public string? TlsrptCheckDetail { get; set; }

    public DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus { get; set; }
    public DateTimeOffset? DmarcAuthorizationCheckedUtc { get; set; }
    public string? DmarcAuthorizationCheckDetail { get; set; }

    public SpfCheckStatus SpfCheckStatus { get; set; }
    public DateTimeOffset? SpfCheckedUtc { get; set; }
    public string? SpfCheckDetail { get; set; }

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
    public int? HaloClientId { get; set; } // override; null means "use the Group's mapping"

    public List<Report> Reports { get; set; } = [];
    public List<TlsrptReport> TlsrptReports { get; set; } = [];
    public List<Group> Groups { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
}
