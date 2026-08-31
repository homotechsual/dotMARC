namespace DotMarc.MtaSts;

public sealed class MtaStsServingVerifier : IMtaStsServingVerifier
{
    private readonly HttpClient _http;

    public MtaStsServingVerifier(HttpClient http) => _http = http;

    public async Task<bool> IsServingCorrectlyAsync(string domainName, string expectedPolicyText, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://mta-sts.{domainName}/.well-known/mta-sts.txt");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Certificate not issued yet, DNS not propagated everywhere, connection refused — all
            // read the same to the caller: not serving correctly yet, try again next cycle.
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return body == expectedPolicyText;
    }
}
