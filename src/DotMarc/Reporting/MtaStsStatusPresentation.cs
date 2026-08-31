using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps MtaStsStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// MTA-STS tab — same shared-presentation-logic precedent as DmarcStatusPresentation.</summary>
public static class MtaStsStatusPresentation
{
    public static Color GetColor(MtaStsStatus status) => status switch
    {
        MtaStsStatus.Active => Color.Success,
        MtaStsStatus.PendingDns or MtaStsStatus.PendingCertificate => Color.Info,
        MtaStsStatus.Failed => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(MtaStsStatus status) => status switch
    {
        MtaStsStatus.PendingDns => "Waiting for DNS",
        MtaStsStatus.PendingCertificate => "Provisioning certificate",
        MtaStsStatus.Active => "Active",
        MtaStsStatus.Failed => "Failed",
        _ => "Not configured"
    };
}
