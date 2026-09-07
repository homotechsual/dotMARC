using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Dns;

/// <summary>Detects known mail services from a domain's MX (inbox provider) and SPF include:
/// mechanisms (sending service) - informational only, no push/remediation action. Does its own raw
/// MX and TXT queries rather than reusing MxDnsChecker/SpfDnsChecker - same "small, independent
/// checker, no shared abstraction" precedent as every other checker in this codebase (see
/// MxDnsChecker's own doc comment).</summary>
public sealed class MailServiceDetector : IMailServiceDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    private static readonly (string Suffix, string Provider)[] InboxProviders =
    [
        (".mail.protection.outlook.com", "Microsoft 365"),
        ("aspmx.l.google.com", "Google Workspace"),
        (".zoho.com", "Zoho Mail"),
        (".zohomail.com", "Zoho Mail"),
        (".zohomail.eu", "Zoho Mail"),
        (".zohomail.in", "Zoho Mail"),
        (".messagingengine.com", "Fastmail"),
        (".protonmail.ch", "ProtonMail")
    ];

    private static readonly (string Suffix, string Provider)[] SendingServices =
    [
        ("_spf.google.com", "Google Workspace"),
        ("spf.protection.outlook.com", "Microsoft 365"),
        ("zoho.com", "Zoho"),
        ("zoho.eu", "Zoho"),
        ("servers.mcsv.net", "Mailchimp"),
        ("sendgrid.net", "SendGrid"),
        ("amazonses.com", "Amazon SES"),
        ("_spf.salesforce.com", "Salesforce")
    ];

    public MailServiceDetector(HttpClient http) => _http = http;

    public async Task<List<DetectedMailService>> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        var results = new List<DetectedMailService>();

        var mxExchanges = await QueryMxExchangesAsync(domainName, cancellationToken).ConfigureAwait(false);
        foreach (var (suffix, provider) in InboxProviders)
        {
            if (results.Any(r => r.Kind == DetectedMailServiceKind.Inbox && r.ProviderName == provider))
            {
                continue;
            }
            if (mxExchanges.Any(exchange => exchange.TrimEnd('.').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new DetectedMailService(provider, DetectedMailServiceKind.Inbox));
            }
        }

        var spfIncludeHosts = await QuerySpfIncludeHostsAsync(domainName, cancellationToken).ConfigureAwait(false);
        foreach (var (suffix, provider) in SendingServices)
        {
            if (results.Any(r => r.Kind == DetectedMailServiceKind.Sending && r.ProviderName == provider))
            {
                continue;
            }
            if (spfIncludeHosts.Any(host => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new DetectedMailService(provider, DetectedMailServiceKind.Sending));
            }
        }

        return results;
    }

    private async Task<List<string>> QueryMxExchangesAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=MX");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var exchanges = new List<string>();
        foreach (var answer in (parsed.Answer ?? []).Where(a => a.Type == 15))
        {
            var parts = answer.Data.Split(' ', 2);
            if (parts.Length == 2)
            {
                exchanges.Add(parts[1]);
            }
        }
        return exchanges;
    }

    private async Task<List<string>> QuerySpfIncludeHostsAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var txtRecords = (parsed.Answer ?? [])
            .Where(a => a.Type == 16)
            .Select(a => string.Join("", a.Data.Split("\" \"")).Trim('"'));

        var spfRecord = txtRecords.FirstOrDefault(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));
        if (spfRecord is null)
        {
            return [];
        }

        return spfRecord
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
            .Select(token => token["include:".Length..])
            .ToList();
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
