using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;
using DmarcRua;
using DotMarc.Data;

namespace DotMarc.Ingestion;

/// <summary>Wraps DmarcRua's AggregateReport parser. DmarcRua itself only throws for input it
/// cannot deserialize as XML at all (e.g. garbage bytes) - well-formed XML that fails schema
/// validation instead sets ValidReport = false without throwing (confirmed empirically against
/// DmarcRua 2.0.1). This wrapper treats both cases identically as failures, since PollingService's
/// failure handling (Task 6) needs a single exception type to catch.</summary>
public static class DmarcReportParser
{
    public static ParsedReport Parse(byte[] xmlBytes)
    {
        var report = LoadReport(xmlBytes);

        if (!report.ValidReport || report.Feedback is null)
        {
            throw new InvalidDataException("DMARC aggregate report failed schema validation.");
        }

        var feedback = report.Feedback;
        var records = feedback.Record.Select(r => new ParsedReportRecord(
            r.Row.SourceIp,
            r.Row.Count,
            r.Row.PolicyEvaluated.Disposition.ToString(),
            r.Row.PolicyEvaluated.Spf.ToString(),
            r.Row.PolicyEvaluated.Dkim.ToString(),
            r.Identifiers.HeaderFrom,
            MapAuthDetails(r.AuthResults),
            MapOverrideReasons(r.Row.PolicyEvaluated.Reason))).ToList();

        return new ParsedReport(
            feedback.PolicyPublished.Domain,
            feedback.ReportMetadata.OrgName,
            feedback.ReportMetadata.ReportId,
            DateTimeOffset.FromUnixTimeSeconds(feedback.ReportMetadata.DateRange.Begin),
            DateTimeOffset.FromUnixTimeSeconds(feedback.ReportMetadata.DateRange.End),
            records);
    }

    private static AggregateReport LoadReport(byte[] xmlBytes)
    {
        if (TryDeserialize(xmlBytes, out var report, out var firstError))
        {
            return report;
        }

        // DmarcRua deserializes with strict, case-sensitive enums, so a report that deviates from
        // the schema in small, well-understood ways (capitalised values, "no policy") is rejected
        // outright and, being left unread in the mailbox, retried on every poll forever. Retry once
        // with those values normalised. Reports that already parse never take this path.
        var normalized = NormalizeEnumValues(xmlBytes);
        if (normalized is not null && TryDeserialize(normalized, out report, out _))
        {
            return report;
        }

        throw new InvalidDataException("Could not deserialize DMARC aggregate report XML.", firstError);
    }

    private static bool TryDeserialize(byte[] xmlBytes, [NotNullWhen(true)] out AggregateReport? report, out Exception? error)
    {
        try
        {
            using var stream = new MemoryStream(xmlBytes);
            report = new AggregateReport(stream);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report = null;
            error = ex;
            return false;
        }
    }

    /// <summary>The report elements whose text is a schema enum, as "parent/element" local names.
    /// Every enum in the schema is lowercase, so these are the only values safe to case-fold.</summary>
    private static readonly HashSet<string> EnumValuedElements =
    [
        "policy_published/p", "policy_published/sp", "policy_published/np",
        "policy_published/adkim", "policy_published/aspf",
        "policy_evaluated/disposition", "policy_evaluated/dkim", "policy_evaluated/spf",
        "reason/type",
        "dkim/result", "spf/result", "spf/scope"
    ];

    /// <summary>Returns the report re-serialized with its enum-valued elements normalised, or null
    /// when the XML can't be read at all or nothing needed changing (so a retry would be pointless).
    /// The document is saved back in its own declared encoding, so non-ASCII text survives.</summary>
    private static byte[]? NormalizeEnumValues(byte[] xmlBytes)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(xmlBytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var changed = false;
        foreach (var element in document.Descendants().Where(e => !e.HasElements && e.Parent is not null).ToList())
        {
            var elementName = element.Name.LocalName;
            if (!EnumValuedElements.Contains($"{element.Parent!.Name.LocalName}/{elementName}"))
            {
                continue;
            }

            var normalized = NormalizeEnumValue(elementName, element.Value);
            if (normalized != element.Value)
            {
                element.Value = normalized;
                changed = true;
            }
        }

        if (!changed)
        {
            return null;
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static string NormalizeEnumValue(string elementName, string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        // Some reporters write "no policy" where the schema's policy/disposition value is "none".
        var isPolicyValue = elementName is "disposition" or "p" or "sp" or "np";
        return isPolicyValue && normalized == "no policy" ? "none" : normalized;
    }

    private static List<ParsedAuthDetail> MapAuthDetails(AuthResultType authResults)
    {
        var details = new List<ParsedAuthDetail>();

        foreach (var dkim in authResults.Dkim ?? [])
        {
            details.Add(new ParsedAuthDetail(
                DmarcAuthMechanism.Dkim,
                dkim.Domain,
                Enum.Parse<DmarcMechanismResult>(dkim.Result.ToString()),
                dkim.Selector,
                null,
                dkim.HumanResult));
        }

        foreach (var spf in authResults.Spf ?? [])
        {
            details.Add(new ParsedAuthDetail(
                DmarcAuthMechanism.Spf,
                spf.Domain,
                Enum.Parse<DmarcMechanismResult>(spf.Result.ToString()),
                null,
                spf.Scope?.ToString(),
                spf.HumanResult));
        }

        return details;
    }

    private static List<ParsedPolicyOverrideReason> MapOverrideReasons(PolicyOverrideReason[]? reasons) =>
        (reasons ?? []).Select(r => new ParsedPolicyOverrideReason(Enum.Parse<DmarcPolicyOverrideType>(r.Type.ToString()), r.Comment)).ToList();
}
