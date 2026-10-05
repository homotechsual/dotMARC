using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class TxtRecordLookupTests
{
    private static (TxtRecordLookup Lookup, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new TxtRecordLookup(new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") }), handler);
    }

    [Fact]
    public async Task GetTxtValues_ReturnsEveryTxtValue_WithChunksJoined()
    {
        var (lookup, handler) = Create();
        handler.ResponseBody = """
            {"Status":0,"Answer":[
              {"type":16,"data":"\"google-site-verification=abc\""},
              {"type":16,"data":"\"v=spf1 include:_spf.google.com \" \"~all\""}
            ]}
            """;

        var values = await lookup.GetTxtValuesAsync("contoso.com", CancellationToken.None);

        Assert.Equal(["google-site-verification=abc", "v=spf1 include:_spf.google.com ~all"], values);
        Assert.Contains("name=contoso.com", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GetTxtValues_IsEmpty_ForNxDomain()
    {
        var (lookup, handler) = Create();
        handler.ResponseBody = """{"Status":3}""";

        Assert.Empty(await lookup.GetTxtValuesAsync("nothing.example", CancellationToken.None));
    }

    [Fact]
    public async Task LookupWithCname_NotesTheCnameHop()
    {
        var (lookup, handler) = Create();
        handler.ResponseBody = """
            {"Status":0,"Answer":[
              {"type":5,"data":"selector1-contoso-com._domainkey.contoso.onmicrosoft.com."},
              {"type":16,"data":"\"v=DKIM1; k=rsa; p=MIIBIjAN\""}
            ]}
            """;

        var result = await lookup.LookupWithCnameAsync("selector1._domainkey.contoso.com", CancellationToken.None);

        Assert.Equal("selector1-contoso-com._domainkey.contoso.onmicrosoft.com.", result.DelegatedToCname);
        Assert.Equal("v=DKIM1; k=rsa; p=MIIBIjAN", result.DirectValue);
    }

    [Fact]
    public async Task CachingLookup_AsksEachNameOnce()
    {
        var inner = new FakeTxtRecordLookup();
        inner.TxtByName["a.example"] = ["v=spf1 -all"];
        var caching = new CachingTxtRecordLookup(inner);

        await caching.GetTxtValuesAsync("a.example", CancellationToken.None);
        await caching.GetTxtValuesAsync("A.example", CancellationToken.None);

        Assert.Single(inner.Queried);
    }

    [Fact]
    public async Task AResolverFailure_IsAnError_NotAnEmptyAnswer()
    {
        // SERVFAIL comes back as HTTP 200 with Status 2 and no answers; reading that as "no record" would invent a
        // missing SPF include, or let a push start from nothing and add a second SPF record.
        var (lookup, handler) = Create();
        handler.ResponseBody = """{"Status":2}""";

        await Assert.ThrowsAsync<HttpRequestException>(() => lookup.GetTxtValuesAsync("contoso.com", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => lookup.LookupWithCnameAsync("contoso.com", CancellationToken.None));
    }
}
