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
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions);

        // 0 is an answer and 3 (NXDOMAIN) is a real "nothing here". Anything else, such as SERVFAIL, means the resolver
        // couldn't find out, which must not be read as "no record".
        if (parsed is null || parsed.Status is not (0 or 3))
        {
            throw new HttpRequestException($"The DNS resolver couldn't look up {name} (status {parsed?.Status}).");
        }

        return parsed.Answer ?? [];
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
