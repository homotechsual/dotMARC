using System.Globalization;
using DotMarc.Portal;

namespace DotMarc.Reporting.ClientReports;

public sealed record ClientReport(
    ResolvedBrand Brand, byte[]? Logo, string GroupName, ReportPeriod Period, string TimeZoneId, string Verdict,
    IReadOnlyList<ClientReportDomain> Domains, IReadOnlyList<string> NextSteps, DateTimeOffset GeneratedUtc,
    string NumberFormat = ReportSettings.DefaultNumberFormat)
{
    public CultureInfo Culture => ReportSettingsService.ResolveNumberFormat(NumberFormat);
}

public sealed record ClientReportDomain(
    string Name, PortalDomainStatus Status, long Messages, double? PassRate, double? PreviousPassRate, IReadOnlyList<double?> Trend,
    string PolicySentence, IReadOnlyList<HealthRow> Health, IReadOnlyList<ClientReportSender> TopSenders, ReceiverActions Receivers,
    IReadOnlyList<ClientReportAlert> Alerts)
{
    /// <summary>The pass rate's change from the comparison period in percentage points, "new" when that period had no
    /// mail, or empty when this one had none.</summary>
    public string ChangeText => FormatChange(CultureInfo.InvariantCulture);

    public string FormatChange(CultureInfo culture) => PassRate is not { } rate
        ? ""
        : PreviousPassRate is not { } previous
            ? "new"
            : string.Create(culture, $"{(rate - previous) * 100:+0.0;-0.0;0.0} pts");
}

public sealed record ClientReportSender(string Ip, string? Owner, long Messages, long Passing, long Failing, double Share);

public sealed record ReceiverActions(long Delivered, long Quarantined, long Rejected, long FailingDelivered);

public sealed record ClientReportAlert(string Title, DateTimeOffset CreatedUtc, DateTimeOffset? ResolvedUtc);
