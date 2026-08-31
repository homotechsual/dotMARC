using DotMarc.MtaSts;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.MtaSts;

public class MtaStsDnsVerifierTests
{
    private static (MtaStsDnsVerifier verifier, FakeHttpMessageHandler handler) CreateVerifier()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new MtaStsDnsVerifier(http), handler);
    }

    [Fact]
    public async Task VerifyAsync_ReturnsNotFound_WhenNoCnameRecordExists()
    {
        var (verifier, handler) = CreateVerifier();
        handler.ResponseBody = """{"Status":3}""";

        var result = await verifier.VerifyAsync("contoso.io", "mta-sts.dotmarc.app", CancellationToken.None);

        Assert.Equal(MtaStsDnsVerificationResult.NotFound, result);
        Assert.Contains("mta-sts.contoso.io", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("type=CNAME", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task VerifyAsync_ReturnsResolved_WhenCnameTargetMatchesExactly()
    {
        var (verifier, handler) = CreateVerifier();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":5,"data":"mta-sts.dotmarc.app."}]}
            """;

        var result = await verifier.VerifyAsync("contoso.io", "mta-sts.dotmarc.app", CancellationToken.None);

        Assert.Equal(MtaStsDnsVerificationResult.Resolved, result);
    }

    [Fact]
    public async Task VerifyAsync_ReturnsPointsElsewhere_WhenCnameTargetsADifferentHost()
    {
        var (verifier, handler) = CreateVerifier();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":5,"data":"somewhere-else.example.com."}]}
            """;

        var result = await verifier.VerifyAsync("contoso.io", "mta-sts.dotmarc.app", CancellationToken.None);

        Assert.Equal(MtaStsDnsVerificationResult.PointsElsewhere, result);
    }

    [Fact]
    public async Task VerifyAsync_IgnoresNonCnameAnswers_AndTrailingDotDifferences()
    {
        var (verifier, handler) = CreateVerifier();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"unrelated txt record\""},{"type":5,"data":"mta-sts.dotmarc.app"}]}
            """;

        var result = await verifier.VerifyAsync("contoso.io", "mta-sts.dotmarc.app.", CancellationToken.None);

        Assert.Equal(MtaStsDnsVerificationResult.Resolved, result);
    }
}
