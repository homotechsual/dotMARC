// test/DotMarc.Tests/IpEnrichment/IpRangeMatcherTests.cs
using DotMarc.Data;
using DotMarc.IpEnrichment;
using Xunit;

namespace DotMarc.Tests.IpEnrichment;

public sealed class IpRangeMatcherTests
{
    [Fact]
    public void FindContaining_ReturnsTheRange_WhenTheIpv6AddressIsWithinBounds()
    {
        // The exact scenario reported: 2a01:111:f403:c207::3 falls within Microsoft's
        // 2a01:110::/31 RIPE allocation.
        var range = new IpRange { RangeStart = "2a01:110::", RangeEnd = "2a01:111:ffff:ffff:ffff:ffff:ffff:ffff", Organization = "Microsoft Limited", Country = "GB" };

        var match = IpRangeMatcher.FindContaining([range], "2a01:111:f403:c207::3");

        Assert.Same(range, match);
    }

    [Fact]
    public void FindContaining_ReturnsTheRange_WhenTheIpv4AddressIsWithinBounds()
    {
        var range = new IpRange { RangeStart = "203.0.113.0", RangeEnd = "203.0.113.255" };

        var match = IpRangeMatcher.FindContaining([range], "203.0.113.42");

        Assert.Same(range, match);
    }

    [Fact]
    public void FindContaining_ReturnsNull_WhenTheIpIsOutsideEveryRange()
    {
        var range = new IpRange { RangeStart = "203.0.113.0", RangeEnd = "203.0.113.255" };

        var match = IpRangeMatcher.FindContaining([range], "198.51.100.1");

        Assert.Null(match);
    }

    [Fact]
    public void FindContaining_ReturnsNull_WhenTheOnlyRangeIsADifferentAddressFamily()
    {
        var range = new IpRange { RangeStart = "2a01:110::", RangeEnd = "2a01:111:ffff:ffff:ffff:ffff:ffff:ffff" };

        var match = IpRangeMatcher.FindContaining([range], "203.0.113.42");

        Assert.Null(match);
    }

    [Fact]
    public void FindContaining_ReturnsNull_WhenTheIpIsUnparsable()
    {
        var range = new IpRange { RangeStart = "203.0.113.0", RangeEnd = "203.0.113.255" };

        var match = IpRangeMatcher.FindContaining([range], "not-an-ip");

        Assert.Null(match);
    }

    [Fact]
    public void FindContaining_ReturnsNull_WhenThereAreNoRanges()
    {
        var match = IpRangeMatcher.FindContaining([], "203.0.113.42");

        Assert.Null(match);
    }

    [Fact]
    public void FindContaining_TreatsBoundsAsInclusive()
    {
        var range = new IpRange { RangeStart = "203.0.113.0", RangeEnd = "203.0.113.255" };

        Assert.Same(range, IpRangeMatcher.FindContaining([range], "203.0.113.0"));
        Assert.Same(range, IpRangeMatcher.FindContaining([range], "203.0.113.255"));
    }
}
