namespace DotMarc.DnsPush;

/// <summary>Tells a DMARC record name from any other.</summary>
internal static class DmarcRecordName
{
    /// <summary>True when the name starts with _dmarc. (ignoring case), the label a domain's DMARC record lives under.</summary>
    internal static bool IsDmarcName(string name) => name.StartsWith("_dmarc.", StringComparison.OrdinalIgnoreCase);
}
