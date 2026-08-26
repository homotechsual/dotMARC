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
        DmarcCheckStatus.Ok => Color.Success,
        DmarcCheckStatus.MissingAuthorizationRecord => Color.Warning,
        DmarcCheckStatus.MissingOwnRecord or DmarcCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(DmarcCheckStatus status) => status switch
    {
        DmarcCheckStatus.Ok => "OK",
        DmarcCheckStatus.MissingOwnRecord => "No DMARC record",
        DmarcCheckStatus.Misconfigured => "Misconfigured",
        DmarcCheckStatus.MissingAuthorizationRecord => "Missing authorization",
        _ => "Not checked yet"
    };
}
