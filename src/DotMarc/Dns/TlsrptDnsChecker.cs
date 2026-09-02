using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks the RFC 8460 SMTP TLS Reporting TXT record at _smtp._tls.&lt;domain&gt;.</summary>
public sealed class TlsrptDnsChecker : ITlsrptDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public TlsrptDnsChecker(HttpClient http) => _http = http;

    public async Task<TlsrptCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        var recordName = $"_smtp._tls.{domainName}";
        var record = await QueryTxtAsync(recordName, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new TlsrptCheckResult(TlsrptCheckStatus.MissingOwnRecord, $"No TXT record found at {recordName}");
        }

        if (!record.StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase))
        {
            return new TlsrptCheckResult(TlsrptCheckStatus.Misconfigured, $"{recordName} does not start with v=TLSRPTv1: {record}");
        }

        var ruaAddresses = ParseRuaAddresses(record);
        if (!ruaAddresses.Any(address => string.Equals(address, mailboxAddress, StringComparison.OrdinalIgnoreCase)))
        {
            return new TlsrptCheckResult(
                TlsrptCheckStatus.Misconfigured,
                ruaAddresses.Count == 0
                    ? $"{recordName} has no rua= tag"
                    : $"{recordName}'s rua= points to {string.Join(", ", ruaAddresses)}, not {mailboxAddress}");
        }

        return new TlsrptCheckResult(TlsrptCheckStatus.Ok, null);
    }

    private async Task<string?> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        var answer = parsed.Answer?.FirstOrDefault(item => item.Type == 16);
        return answer is null ? null : string.Join("", answer.Data.Split("\" \"")).Trim('"');
    }

    private static List<string> ParseRuaAddresses(string record) => record
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(part => part.StartsWith("rua=", StringComparison.OrdinalIgnoreCase))
        .SelectMany(part => part["rua=".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Where(uri => uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        .Select(uri => uri["mailto:".Length..])
        .ToList();

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}