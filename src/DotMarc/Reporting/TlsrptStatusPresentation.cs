using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

public static class TlsrptStatusPresentation
{
    public static Color GetColor(TlsrptCheckStatus status) => status switch
    {
        TlsrptCheckStatus.Ok => Color.Success,
        TlsrptCheckStatus.MissingOwnRecord or TlsrptCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(TlsrptCheckStatus status) => status switch
    {
        TlsrptCheckStatus.Ok => "OK",
        TlsrptCheckStatus.MissingOwnRecord => "No TLSRPT record",
        TlsrptCheckStatus.Misconfigured => "Misconfigured",
        _ => "Not checked yet"
    };
}