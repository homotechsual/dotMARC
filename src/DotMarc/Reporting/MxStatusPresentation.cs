using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps MxCheckStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist - same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class MxStatusPresentation
{
    public static Color GetColor(MxCheckStatus status) => status switch
    {
        MxCheckStatus.Ok => Color.Success,
        MxCheckStatus.UnresolvableTarget => Color.Warning,
        MxCheckStatus.MissingRecord => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(MxCheckStatus status) => status switch
    {
        MxCheckStatus.Ok => "OK",
        MxCheckStatus.MissingRecord => "No MX record",
        MxCheckStatus.UnresolvableTarget => "Target does not resolve",
        _ => "Not checked yet"
    };
}
