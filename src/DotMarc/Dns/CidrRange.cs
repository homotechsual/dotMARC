using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DotMarc.Dns;

/// <summary>An IP range such as <c>192.0.2.0/24</c>. A bare address is a single-host range. An IPv4-mapped IPv6 address
/// (<c>::ffff:192.0.2.5</c>) is treated as the IPv4 address it wraps, so it matches IPv4 ranges.</summary>
public readonly record struct CidrRange(IPAddress Network, int PrefixLength)
{
    public static bool TryParse(string? text, out CidrRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var slashIndex = text.IndexOf('/');
        var addressText = slashIndex < 0 ? text : text[..slashIndex];
        if (!IPAddress.TryParse(addressText.Trim(), out var address))
        {
            return false;
        }

        address = Normalise(address);
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (slashIndex >= 0
            && (!int.TryParse(text[(slashIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > maxPrefix))
        {
            return false;
        }

        range = new CidrRange(address, prefix);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        var candidate = Normalise(address);
        if (candidate.AddressFamily != Network.AddressFamily)
        {
            return false;
        }

        var network = Network.GetAddressBytes();
        var bytes = candidate.GetAddressBytes();
        var wholeBytes = PrefixLength / 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            if (network[index] != bytes[index])
            {
                return false;
            }
        }

        var remainingBits = PrefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (network[wholeBytes] & mask) == (bytes[wholeBytes] & mask);
    }

    public override string ToString() => $"{Network}/{PrefixLength}";

    private static IPAddress Normalise(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
