using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace DotMarc.Graph;

public sealed class ConfidentialClientGraphTokenProvider : IGraphTokenProvider
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    private readonly IConfidentialClientApplication _app;

    public ConfidentialClientGraphTokenProvider(IOptions<GraphOptions> options)
    {
        var graphOptions = options.Value;
        _app = ConfidentialClientApplicationBuilder.Create(graphOptions.ClientId)
            .WithClientSecret(graphOptions.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{graphOptions.TenantId}")
            .Build();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var result = await _app.AcquireTokenForClient(Scopes).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return result.AccessToken;
    }
}
