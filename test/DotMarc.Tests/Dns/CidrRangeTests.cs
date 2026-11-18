using System.Net;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class CidrRangeTests
{
    private static bool Contains(string range, string address)
    {
        Assert.True(CidrRange.TryParse(range, out var parsed), $"{range} should parse");
        return parsed.Contains(IPAddress.Parse(address));
    }

    [Theory]
    [InlineData("192.0.2.0/24", "192.0.2.0", true)]
    [InlineData("192.0.2.0/24", "192.0.2.255", true)]
    [InlineData("192.0.2.0/24", "192.0.3.0", false)]
    [InlineData("192.0.2.0/24", "192.0.1.255", false)]
    [InlineData("192.0.2.7", "192.0.2.7", true)]
    [InlineData("192.0.2.7", "192.0.2.8", false)]
    [InlineData("0.0.0.0/0", "203.0.113.9", true)]
    [InlineData("203.0.113.9/32", "203.0.113.9", true)]
    [InlineData("203.0.113.9/32", "203.0.113.10", false)]
    [InlineData("192.0.2.77/24", "192.0.2.1", true)]
    [InlineData("192.0.2.77/24", "192.0.3.1", false)]
    [InlineData("2001:db8::/32", "2001:db8:ffff::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8::1", "2001:db8::1", true)]
    [InlineData("192.0.2.0/24", "::ffff:192.0.2.5", true)]
    [InlineData("192.0.2.0/24", "::ffff:192.0.3.5", false)]
    [InlineData("192.0.2.0/24", "2001:db8::1", false)]
    [InlineData("2001:db8::/32", "192.0.2.5", false)]
    public void Contains_FollowsThePrefix(string range, string address, bool expected) =>
        Assert.Equal(expected, Contains(range, address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("999.1.1.1")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3.4/-1")]
    [InlineData("1.2.3.4/")]
    [InlineData("::1/129")]
    [InlineData("zzz")]
    public void TryParse_RejectsMalformedRanges(string? text) =>
        Assert.False(CidrRange.TryParse(text, out _));

    [Fact]
    public void ToString_WritesTheRange()
    {
        Assert.True(CidrRange.TryParse("192.0.2.0/24", out var range));
        Assert.Equal("192.0.2.0/24", range.ToString());
    }
}
