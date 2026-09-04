namespace DotMarc.DnsPush;

/// <summary>Shared answer-chain parsing for DmarcTxtLookup/TlsrptTxtLookup: both query type=TXT
/// against a name that might actually be a CNAME to somewhere else (e.g. a domain's _dmarc record
/// delegated to a third-party DMARC monitoring service). A plain TXT query transparently follows
/// CNAMEs, so the raw DNS-over-HTTPS answer array is the only place that hop is still visible —
/// type 5 is CNAME, type 16 is TXT, per standard DNS RR type numbers.</summary>
public static class DnsRecordLookupParsing
{
    public static DnsRecordLookupResult ParseTxtWithCnameDetection(IEnumerable<(int Type, string Data)>? answers)
    {
        if (answers is null)
        {
            return new DnsRecordLookupResult(null, null);
        }

        var list = answers.ToList();
        var cname = list.FirstOrDefault(a => a.Type == 5).Data;
        var txt = list.FirstOrDefault(a => a.Type == 16);
        var directValue = txt.Data is null ? null : string.Join("", txt.Data.Split("\" \"")).Trim('"');
        return new DnsRecordLookupResult(directValue, cname);
    }
}
