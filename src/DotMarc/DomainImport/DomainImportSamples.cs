namespace DotMarc.DomainImport;

/// <summary>The sample files offered on the Import domains page, built from one set of rows so the CSV and the .xlsx
/// always say the same thing.</summary>
public static class DomainImportSamples
{
    private static readonly string[][] Rows =
    [
        ["domain", "groups", "tags", "halo client", "monitored", "dkim selectors", "mta-sts mode", "mta-sts mx hosts", "mta-sts max age", "connectwise company", "autotask company"],
        ["contoso.com", "Contoso", "primary", "Contoso Ltd", "yes", "selector1;selector2", "testing", "mail.contoso.com", "604800", "Contoso Limited", "Contoso Ltd"],
        ["fabrikam.com", "Fabrikam;Europe", "", "", "yes", "", "", "", "", "", ""],
        ["old.contoso.com", "Contoso", "-primary", "", "no", "", "off", "", "", "", ""],
    ];

    public static string Csv { get; } = string.Join("\r\n", Rows.Select(row => string.Join(',', row.Select(QuoteIfNeeded)))) + "\r\n";

    public static byte[] Xlsx() =>
        SimpleXlsxWriter.Build(Rows.Select(row => (IReadOnlyList<object?>)row.Select(cell => cell.Length == 0 ? null : (object?)cell).ToList()).ToList());

    private static string QuoteIfNeeded(string cell) =>
        cell.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{cell.Replace("\"", "\"\"")}\"" : cell;
}
