using DotMarc.Data;
using DotMarc.Baselines;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DmarcCheckStatus to the MudBlazor color/label pair used consistently everywhere
/// it's displayed - Dashboard.razor's DNS Status column and DomainDetail.razor's DMARC record
/// status panel - following the same shared-presentation-logic precedent as DomainStatistics.</summary>
public static class DmarcStatusPresentation
{
    public static Color GetColor(DmarcCheckStatus status, DmarcAuthorizationCheckStatus authorizationStatus, DmarcTargetOutcome target)
    {
        var color = GetColor(status, authorizationStatus);
        return color != Color.Error && target == DmarcTargetOutcome.Below ? Color.Warning : color;
    }

    public static string GetLabel(DmarcCheckStatus status, DmarcAuthorizationCheckStatus authorizationStatus, DmarcTargetOutcome target) =>
        $"{GetLabel(status, authorizationStatus)}{(target == DmarcTargetOutcome.Below ? "; below target" : target == DmarcTargetOutcome.NeedsChecking ? "; target needs checking" : "")}";

    public static Color GetColor(DmarcCheckStatus status) => status switch
    {
        DmarcCheckStatus.Ok or DmarcCheckStatus.MissingAuthorizationRecord => Color.Success,
        // Information, not a fault: dotMARC's mailbox is there, alongside one other address.
        DmarcCheckStatus.OtherReportAddresses => Color.Info,
        DmarcCheckStatus.TooManyReportAddresses => Color.Warning,
        DmarcCheckStatus.MissingOwnRecord or DmarcCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(DmarcCheckStatus status) => status switch
    {
        DmarcCheckStatus.Ok or DmarcCheckStatus.MissingAuthorizationRecord => "OK",
        DmarcCheckStatus.OtherReportAddresses => "OK, other rua",
        DmarcCheckStatus.TooManyReportAddresses => "Too many rua addresses",
        DmarcCheckStatus.MissingOwnRecord => "No DMARC record",
        DmarcCheckStatus.Misconfigured => "Misconfigured",
        _ => "Not checked yet"
    };

    /// <summary>True when the own record points reports at dotMARC's mailbox without a fault, so only the
    /// authorization record can still hold the combined status back.</summary>
    private static bool OwnRecordIsFine(DmarcCheckStatus status) =>
        status is DmarcCheckStatus.Ok or DmarcCheckStatus.OtherReportAddresses;

    /// <summary>Combines the two independent DMARC checks into one chip's worth of color/label - 
    /// used only by Dashboard.razor's single DMARC cell, which (per the design spec's Non-goals)
    /// intentionally doesn't grow a second column for the authorization check the way
    /// DomainDetail.razor's Overview tab does. Reproduces the pre-split single-DmarcCheckStatus
    /// behavior: an own-record problem always wins (it's the more severe fault), and only when the
    /// own record is fine does a missing authorization record show through - otherwise a domain
    /// whose only DMARC fault is a missing cross-domain authorization record would silently read as
    /// a plain "OK" on the Dashboard, losing a real fault signal from the app's primary at-a-glance
    /// page.</summary>
    public static Color GetColor(DmarcCheckStatus status, DmarcAuthorizationCheckStatus authorizationStatus)
    {
        if (!OwnRecordIsFine(status))
        {
            return GetColor(status);
        }
        return authorizationStatus == DmarcAuthorizationCheckStatus.Missing ? Color.Warning : GetColor(status);
    }

    public static string GetLabel(DmarcCheckStatus status, DmarcAuthorizationCheckStatus authorizationStatus)
    {
        if (!OwnRecordIsFine(status))
        {
            return GetLabel(status);
        }
        return authorizationStatus == DmarcAuthorizationCheckStatus.Missing ? "Missing authorization" : GetLabel(status);
    }
}
