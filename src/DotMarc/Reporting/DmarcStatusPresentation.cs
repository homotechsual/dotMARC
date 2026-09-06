using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DmarcCheckStatus to the MudBlazor color/label pair used consistently everywhere
/// it's displayed — Dashboard.razor's DNS Status column and DomainDetail.razor's DMARC record
/// status panel — following the same shared-presentation-logic precedent as DomainStatistics.</summary>
public static class DmarcStatusPresentation
{
    public static Color GetColor(DmarcCheckStatus status) => status switch
    {
        DmarcCheckStatus.Ok or DmarcCheckStatus.MissingAuthorizationRecord => Color.Success,
        DmarcCheckStatus.MissingOwnRecord or DmarcCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(DmarcCheckStatus status) => status switch
    {
        DmarcCheckStatus.Ok or DmarcCheckStatus.MissingAuthorizationRecord => "OK",
        DmarcCheckStatus.MissingOwnRecord => "No DMARC record",
        DmarcCheckStatus.Misconfigured => "Misconfigured",
        _ => "Not checked yet"
    };

    /// <summary>Combines the two independent DMARC checks into one chip's worth of color/label —
    /// used only by Dashboard.razor's single DMARC cell, which (per the design spec's Non-goals)
    /// intentionally doesn't grow a second column for the authorization check the way
    /// DomainDetail.razor's Overview tab does. Reproduces the pre-split single-DmarcCheckStatus
    /// behavior: an own-record problem always wins (it's the more severe fault), and only when the
    /// own record is fine does a missing authorization record show through — otherwise a domain
    /// whose only DMARC fault is a missing cross-domain authorization record would silently read as
    /// a plain "OK" on the Dashboard, losing a real fault signal from the app's primary at-a-glance
    /// page.</summary>
    public static Color GetColor(DmarcCheckStatus status, DmarcAuthorizationCheckStatus authorizationStatus)
    {
        if (status != DmarcCheckStatus.Ok)
        {
            return GetColor(status);
        }
        return authorizationStatus == DmarcAuthorizationCheckStatus.Missing ? Color.Warning : Color.Success;
    }

    public static string GetLabel(DmarcCheckStatus status, DmarcAuthorizationCheckStatus authorizationStatus)
    {
        if (status != DmarcCheckStatus.Ok)
        {
            return GetLabel(status);
        }
        return authorizationStatus == DmarcAuthorizationCheckStatus.Missing ? "Missing authorization" : "OK";
    }
}
