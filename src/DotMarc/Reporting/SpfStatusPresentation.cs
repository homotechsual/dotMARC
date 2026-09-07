using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps SpfCheckStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist - same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class SpfStatusPresentation
{
    public static Color GetColor(SpfCheckStatus status) => status switch
    {
        SpfCheckStatus.Ok or SpfCheckStatus.NullSpf => Color.Success,
        SpfCheckStatus.MultipleRecords => Color.Warning,
        SpfCheckStatus.MissingRecord or SpfCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(SpfCheckStatus status) => status switch
    {
        SpfCheckStatus.Ok => "OK",
        SpfCheckStatus.NullSpf => "Null SPF (no senders)",
        SpfCheckStatus.MissingRecord => "No SPF record",
        SpfCheckStatus.MultipleRecords => "Multiple SPF records",
        SpfCheckStatus.Misconfigured => "Misconfigured",
        _ => "Not checked yet"
    };
}
