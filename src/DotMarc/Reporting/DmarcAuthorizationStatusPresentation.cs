using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DmarcAuthorizationCheckStatus to the MudBlazor color/label pair used on
/// DomainDetail.razor's Overview health checklist - same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class DmarcAuthorizationStatusPresentation
{
    public static Color GetColor(DmarcAuthorizationCheckStatus status) => status switch
    {
        DmarcAuthorizationCheckStatus.Ok or DmarcAuthorizationCheckStatus.NotApplicable => Color.Success,
        DmarcAuthorizationCheckStatus.Missing => Color.Warning,
        _ => Color.Default
    };

    public static string GetLabel(DmarcAuthorizationCheckStatus status) => status switch
    {
        DmarcAuthorizationCheckStatus.Ok => "OK",
        DmarcAuthorizationCheckStatus.NotApplicable => "Not applicable",
        DmarcAuthorizationCheckStatus.Missing => "Missing authorization",
        _ => "Not checked yet"
    };
}
