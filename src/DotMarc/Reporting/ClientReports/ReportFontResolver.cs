using PdfSharp.Fonts;

namespace DotMarc.Reporting.ClientReports;

/// <summary>Serves the bundled Roboto to PDFsharp, so reports look the same on any server, including a container with
/// no system fonts. Every family name resolves to Roboto; italic is simulated. Also declares PDFsharp's AGPL license, which
/// PDFsharp 6.5 and later require before it will draw anything: dotMARC is AGPL-3.0, so that is the license that applies.</summary>
public sealed class ReportFontResolver : IFontResolver
{
    public const string FamilyName = "Roboto";
    private const string Regular = "Roboto-Regular";
    private const string Bold = "Roboto-Bold";
    private static readonly object InstallLock = new();

    public static void EnsureInstalled()
    {
        lock (InstallLock)
        {
            PdfSharp.PdfSharpLicense.SetAgplLicense();
            GlobalFontSettings.FontResolver ??= new ReportFontResolver();
        }
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? Bold : Regular, mustSimulateBold: false, mustSimulateItalic: isItalic);

    public byte[] GetFont(string faceName)
    {
        using var stream = typeof(ReportFontResolver).Assembly.GetManifestResourceStream($"DotMarc.Reporting.ClientReports.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"The bundled font {faceName} is missing from the build.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
