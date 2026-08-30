// src/DotMarc/IpEnrichment/RdapIpInfoLookup.cs
using System.Net;
using DotMarc.Data;

namespace DotMarc.IpEnrichment;

/// <summary>Looks up one IP's registered organization/country via a single GET to
/// https://rdap.org/ip/{ip} — the public RDAP bootstrap redirector, which forwards to whichever
/// RIR (RIPE, ARIN, APNIC, LACNIC, AFRINIC) actually holds that address block. HttpClient follows
/// the redirect automatically, so this needs no dispatch logic of its own.
///
/// Unlike DmarcDnsChecker (which lets a failed check simply not update Domain.DmarcCheckedUtc, so
/// it's naturally retried on the next uniform 24h cycle), a network failure here is turned into an
/// explicit LookupFailed result rather than left to throw: IpInfoService needs to persist that
/// outcome so a source IP that's briefly unreachable doesn't get re-queried on every single page
/// view before its 24h retry window elapses.</summary>
public sealed class RdapIpInfoLookup : IIpInfoLookup
{
    private readonly HttpClient _http;

    public RdapIpInfoLookup(HttpClient http) => _http = http;

    public async Task<IpLookupResult> LookupAsync(string ip, CancellationToken cancellationToken)
    {
        // Uri.EscapeDataString percent-encodes ':' (e.g. to "%3A"), but rdap.org's redirector
        // 400s on an IPv6 path with encoded colons — every IPv6 lookup failed as a result.
        // IPAddress.ToString()'s canonical form only ever emits digits/hex/colons/dots, all of
        // which are legal unescaped in a URL path segment per RFC 3986, so it's used directly
        // instead of an escaped form of the caller-supplied string. This also validates the
        // input: a SourceIp that isn't actually a parsable IP address (malformed report data)
        // fails fast without making a network call rather than sending rdap.org a nonsense path.
        if (!IPAddress.TryParse(ip, out var parsed))
        {
            return new IpLookupResult(IpLookupStatus.LookupFailed, null, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"ip/{parsed}");
        request.Headers.Accept.ParseAdd("application/rdap+json");
        // The User-Agent header is also set by Program.cs's AddHttpClient<IIpInfoLookup,
        // RdapIpInfoLookup> registration; it's set again here so this lookup is self-sufficient
        // and doesn't silently start getting 403'd by rdap.org's WAF if that DI configuration is
        // ever refactored away — this exact failure mode is what this header fixes.
        request.Headers.UserAgent.ParseAdd("dotMARC (+https://github.com/homotechsual/dotMARC)");

        // The whole request/response/parse pipeline is guarded, not just SendAsync: a 200
        // response with a truncated body, an HTML WAF interstitial, or any other non-JSON success
        // response would otherwise throw out of ReadAsStringAsync/JsonDocument.Parse uncaught,
        // breaking this method's documented invariant that a lookup always resolves to
        // Ok/NotFound/LookupFailed and never throws for an ordinary I/O-shaped failure.
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new IpLookupResult(IpLookupStatus.NotFound, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new IpLookupResult(IpLookupStatus.LookupFailed, null, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (organization, country) = RdapResponseParser.Parse(body);
            var (rangeStart, rangeEnd) = RdapResponseParser.ParseRange(body);
            return new IpLookupResult(IpLookupStatus.Ok, organization, country, rangeStart, rangeEnd);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A genuine cancellation (a live token, not today's CancellationToken.None call
            // sites) should propagate as OperationCanceledException rather than being folded into
            // an ordinary LookupFailed result — a future caller that does pass a live token
            // needs to be able to tell "the caller gave up" apart from "the lookup failed."
            throw;
        }
        catch (Exception)
        {
            return new IpLookupResult(IpLookupStatus.LookupFailed, null, null);
        }
    }
}
