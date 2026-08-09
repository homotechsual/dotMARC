using System.Net;
using System.Text;

namespace DotMarc.Tests.Internal;

/// <summary>Records every request made through it and returns a fixed response body/status for
/// each, matching the sibling oncall-busybar-agent project's own test fake of the same name.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public string ResponseBody { get; set; } = "{}";
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var response = new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}
