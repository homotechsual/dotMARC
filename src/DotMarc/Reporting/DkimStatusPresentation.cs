using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DkimCheckStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist — same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class DkimStatusPresentation
{
    public static Color GetColor(DkimCheckStatus status) => status switch
    {
        DkimCheckStatus.Ok => Color.Success,
        DkimCheckStatus.Missing or DkimCheckStatus.Misconfigured => Color.Warning,
        _ => Color.Default
    };

    public static string GetLabel(DkimCheckStatus status) => status switch
    {
        DkimCheckStatus.Ok => "OK",
        DkimCheckStatus.Missing => "Selector record missing",
        DkimCheckStatus.Misconfigured => "Selector misconfigured",
        _ => "Not configured"
    };
}
