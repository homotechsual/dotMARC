using Microsoft.AspNetCore.Http;

namespace DotMarc.DnsPush;

/// <summary>/dns-push/{provider}/start is a GET, so a link on another site could send a signed-in person to it. Since
/// the SPF push carries the record to publish, that would let someone else choose what lands in a client's DNS. A push
/// only starts when the request came from one of dotMARC's own pages: the browser's Sec-Fetch-Site header says
/// same-origin, or, for a browser that doesn't send it, the Referer is this site.</summary>
public static class DnsPushStartGuard
{
    public static bool IsFromThisSite(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0)
        {
            return string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);
        }

        return Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
            && string.Equals(referer.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(referer.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
