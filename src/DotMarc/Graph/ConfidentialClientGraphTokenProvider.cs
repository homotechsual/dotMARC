using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace DotMarc.Graph;

public sealed class ConfidentialClientGraphTokenProvider : IGraphTokenProvider
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    private readonly Func<bool, CancellationToken, Task<string>> _acquire;
    private int _forceRefresh;

    public ConfidentialClientGraphTokenProvider(IOptions<GraphOptions> options)
    {
        var graphOptions = options.Value;
        var app = ConfidentialClientApplicationBuilder.Create(graphOptions.ClientId)
            .WithClientSecret(graphOptions.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{graphOptions.TenantId}")
            .Build();
        _acquire = async (forceRefresh, cancellationToken) =>
            (await app.AcquireTokenForClient(Scopes).WithForceRefresh(forceRefresh).ExecuteAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
    }

    /// <summary>For tests: the token call, told whether to bypass the cache.</summary>
    internal ConfidentialClientGraphTokenProvider(Func<bool, CancellationToken, Task<string>> acquire) => _acquire = acquire;

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        _acquire(Interlocked.Exchange(ref _forceRefresh, 0) == 1, cancellationToken);

    public void Invalidate() => Interlocked.Exchange(ref _forceRefresh, 1);
}
