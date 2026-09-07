using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class MxDnsCheckerTests
{
    private static (MxDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new MxDnsChecker(http), handler);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingRecord_WhenNoMxRecordsExist()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(MxCheckStatus.MissingRecord, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNullMx_WhenNullMxIsPublished()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":15,"data":"0 ."}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(MxCheckStatus.NullMx, result.Status);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenTheMxTargetResolves()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":15,"data":"10 mail.contoso.io."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":1,"data":"192.0.2.10"}]}""");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(MxCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("type=MX", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("mail.contoso.io", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnresolvableTarget_WhenTheMxTargetHasNoARecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":15,"data":"10 mail.contoso.io."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":3}""");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(MxCheckStatus.UnresolvableTarget, result.Status);
        Assert.Contains("mail.contoso.io", result.Detail);
    }
}
