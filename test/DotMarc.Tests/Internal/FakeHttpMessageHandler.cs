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

    /// <summary>When non-empty, each call dequeues the next body in order — for testing
    /// multi-page (@odata.nextLink) responses. Once drained, falls back to
    /// <see cref="ResponseBody"/> for any further calls.</summary>
    public Queue<string> ResponseBodies { get; } = new();

    /// <summary>When non-empty, each call dequeues the next status code in order — for testing a
    /// sequence like 401-then-200 across a retry. Once drained, falls back to
    /// <see cref="StatusCode"/> for any further calls.</summary>
    public Queue<HttpStatusCode> StatusCodes { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var body = ResponseBodies.Count > 0 ? ResponseBodies.Dequeue() : ResponseBody;
        var statusCode = StatusCodes.Count > 0 ? StatusCodes.Dequeue() : StatusCode;
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}
