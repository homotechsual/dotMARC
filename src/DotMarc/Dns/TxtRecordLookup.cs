using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.DnsPush;

namespace DotMarc.Dns;

public sealed class TxtRecordLookup : ITxtRecordLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public TxtRecordLookup(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken)
    {
        var answers = await QueryAsync(name, cancellationToken).ConfigureAwait(false);
        return answers
            .Where(answer => answer.Type == 16)
            .Select(answer => string.Join("", answer.Data.Split("\" \"")).Trim('"'))
            .ToList();
    }

    public async Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken)
    {
        var answers = await QueryAsync(name, cancellationToken).ConfigureAwait(false);
        return DnsRecordLookupParsing.ParseTxtWithCnameDetection(answers.Select(answer => (answer.Type, answer.Data)));
    }

    private async Task<List<DnsAnswer>> QueryAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)?.Answer ?? [];
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
