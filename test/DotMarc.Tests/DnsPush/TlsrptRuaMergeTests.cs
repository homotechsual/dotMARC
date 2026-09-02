using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class TlsrptRuaMergeTests
{
    [Fact]
    public void TryMerge_ReplacesRuaAndPreservesOtherTags()
    {
        var merged = TlsrptRuaMerge.TryMerge("v=TLSRPTv1; rua=mailto:old@example.com; fo=1", "tlsrpt@reports.example");

        Assert.Equal("v=TLSRPTv1; rua=mailto:tlsrpt@reports.example; fo=1", merged);
    }

    [Fact]
    public void TryMerge_ReturnsNull_ForANonTlsrptRecord()
    {
        Assert.Null(TlsrptRuaMerge.TryMerge("v=DMARC1; p=none", "tlsrpt@reports.example"));
    }
}