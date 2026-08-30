// src/DotMarc/IpEnrichment/IpRangeMatcher.cs
using System.Net;
using DotMarc.Data;

namespace DotMarc.IpEnrichment;

/// <summary>Pure containment check: does a candidate IP fall within any previously cached
/// IpRange? No I/O — matches this codebase's "pure core, thin I/O adapter" convention (see
/// RdapResponseParser). Comparison is done on IPAddress.GetAddressBytes(), which is big-endian
/// (network byte order) for both address families, so lexicographic byte comparison directly
/// matches numeric magnitude order within one family; a range and candidate from different
/// families (e.g. an IPv4 candidate against an IPv6 range) never match.</summary>
public static class IpRangeMatcher
{
    public static IpRange? FindContaining(IReadOnlyList<IpRange> ranges, string ip)
    {
        if (!IPAddress.TryParse(ip, out var candidate))
        {
            return null;
        }

        var candidateBytes = candidate.GetAddressBytes();

        foreach (var range in ranges)
        {
            if (!IPAddress.TryParse(range.RangeStart, out var start) || !IPAddress.TryParse(range.RangeEnd, out var end))
            {
                continue;
            }

            if (start.AddressFamily != candidate.AddressFamily || end.AddressFamily != candidate.AddressFamily)
            {
                continue;
            }

            if (Compare(start.GetAddressBytes(), candidateBytes) <= 0 && Compare(candidateBytes, end.GetAddressBytes()) <= 0)
            {
                return range;
            }
        }

        return null;
    }

    private static int Compare(byte[] a, byte[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var diff = a[i].CompareTo(b[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        return 0;
    }
}
