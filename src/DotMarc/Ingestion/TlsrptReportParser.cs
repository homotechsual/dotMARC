using System.Text.Json;

namespace DotMarc.Ingestion;

public static class TlsrptReportParser
{
    public static ParsedTlsrptReport Parse(byte[] jsonBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonBytes);
            var root = document.RootElement;
            var organization = root.GetProperty("organization-name").GetString();
            var reportId = root.GetProperty("report-id").GetString();
            var dateRange = root.GetProperty("date-range");
            var policies = root.GetProperty("policies").EnumerateArray().Select(ParsePolicy).ToList();
            var domain = policies.Select(policy => policy.PolicyDomain).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(organization) || string.IsNullOrWhiteSpace(reportId) || string.IsNullOrWhiteSpace(domain) || policies.Count == 0)
            {
                throw new InvalidDataException("TLSRPT aggregate report is missing required values.");
            }

            return new ParsedTlsrptReport(domain, organization, reportId,
                DateTimeOffset.Parse(dateRange.GetProperty("start-datetime").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(dateRange.GetProperty("end-datetime").GetString()!, System.Globalization.CultureInfo.InvariantCulture), policies);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("Could not deserialize TLSRPT aggregate report JSON.", exception);
        }
    }

    private static ParsedTlsrptPolicy ParsePolicy(JsonElement element)
    {
        var policy = element.GetProperty("policy");
        var summary = element.GetProperty("summary");
        var details = element.TryGetProperty("failure-details", out var failures)
            ? failures.EnumerateArray().Select(failure => new ParsedTlsrptFailureDetail(
                failure.GetProperty("result-type").GetString()!,
                failure.GetProperty("failed-session-count").GetInt64(),
                failure.TryGetProperty("receiving-mx-hostname", out var hostname) ? hostname.GetString() : null,
                failure.TryGetProperty("failure-reason-code", out var reason) ? reason.GetString() : null,
                failure.TryGetProperty("additional-information", out var information) ? information.GetString() : null)).ToList()
            : [];
        return new ParsedTlsrptPolicy(policy.GetProperty("policy-type").GetString()!, policy.GetProperty("policy-domain").GetString()!,
            summary.GetProperty("total-successful-session-count").GetInt64(), summary.GetProperty("total-failure-session-count").GetInt64(), details);
    }
}