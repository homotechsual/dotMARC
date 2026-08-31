using DotMarc.Data;

namespace DotMarc.MtaSts;

/// <summary>Renders a Domain's hosted MTA-STS policy as the plain-text body RFC 8461 §3.2
/// requires — a pure function, independent of DNS/HTTP/the database, so it needs no fake
/// anything to test.</summary>
public static class MtaStsPolicyRenderer
{
    public static string Render(MtaStsMode mode, IReadOnlyList<string> mxHosts, int maxAgeSeconds)
    {
        var lines = new List<string>
        {
            "version: STSv1",
            $"mode: {mode.ToString().ToLowerInvariant()}"
        };

        lines.AddRange(mxHosts.Select(host => $"mx: {host}"));
        lines.Add($"max_age: {maxAgeSeconds}");

        // RFC 8461 §3.2 requires the body to use CRLF line endings.
        return string.Join("\r\n", lines) + "\r\n";
    }
}
