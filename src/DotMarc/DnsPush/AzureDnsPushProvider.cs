using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Azure DNS via a delegated Entra ID authorization-code
/// exchange — the push only succeeds if the SIGNED-IN USER's own Azure RBAC grants them write
/// access on the target zone; dotMARC never holds a standing grant of its own. Same "nothing
/// persisted" contract as CloudflareDnsPushProvider.</summary>
public sealed class AzureDnsPushProvider : IDnsPushProvider
{
    private const string Scope = "https://management.azure.com/user_impersonation";

    private readonly AzureDnsOptions _options;

    public AzureDnsPushProvider(IOptions<AzureDnsOptions> options) => _options = options.Value;

    public string ProviderKey => "azure-dns";
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_options.TenantId) && !string.IsNullOrEmpty(_options.ClientId) && !string.IsNullOrEmpty(_options.ClientSecret);

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = $"{Scope} openid",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/authorize?" +
            string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var confidentialClient = ConfidentialClientApplicationBuilder.Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{_options.TenantId}")
            .Build();

        AuthenticationResult authResult;
        try
        {
            authResult = await confidentialClient
                .AcquireTokenByAuthorizationCode([Scope], code)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the authorization code exchange: {ex.Message}");
        }

        var armClient = new ArmClient(new FixedTokenCredential(authResult.AccessToken, authResult.ExpiresOn));

        var zoneName = ZoneNameFor(change.Name);
        var zone = await FindZoneAsync(armClient, zoneName, cancellationToken).ConfigureAwait(false);
        if (zone is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound,
                $"Couldn't find {zoneName} in any subscription you authorized — check you have DNS Zone Contributor rights on it.");
        }

        return await PushRecordAsync(zone, zoneName, change, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DnsZoneResource?> FindZoneAsync(ArmClient armClient, string zoneName, CancellationToken cancellationToken)
    {
        await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await foreach (var candidate in subscription.GetDnsZonesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(candidate.Data.Name, zoneName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static async Task<DnsPushResult> PushRecordAsync(DnsZoneResource zone, string zoneName, DnsRecordChange change, CancellationToken cancellationToken)
    {
        // "mta-sts.contoso.co.uk" under zone "contoso.co.uk" -> relative record name "mta-sts".
        var relativeName = change.Name[..^(zoneName.Length + 1)];

        try
        {
            if (string.Equals(change.RecordType, "CNAME", StringComparison.OrdinalIgnoreCase))
            {
                var data = new DnsCnameRecordData { TtlInSeconds = 3600, Cname = change.DesiredValue };
                await zone.GetDnsCnameRecords().CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var data = new DnsTxtRecordData { TtlInSeconds = 3600 };
                data.DnsTxtRecords.Add(new DnsTxtRecordInfo { Values = { change.DesiredValue } });
                await zone.GetDnsTxtRecords().CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the record push: {ex.Message}");
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
    }

    private static string ZoneNameFor(string recordName)
    {
        var firstDot = recordName.IndexOf('.');
        return firstDot < 0 ? recordName : recordName[(firstDot + 1)..];
    }

    /// <summary>Wraps an access token already obtained via the delegated authorization-code
    /// exchange above — ArmClient needs a TokenCredential, but there is nothing for it to actually
    /// fetch here; it already has the one token this whole operation is scoped to.</summary>
    private sealed class FixedTokenCredential : TokenCredential
    {
        private readonly string _accessToken;
        private readonly DateTimeOffset _expiresOn;

        public FixedTokenCredential(string accessToken, DateTimeOffset expiresOn)
        {
            _accessToken = accessToken;
            _expiresOn = expiresOn;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(_accessToken, _expiresOn);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(new AccessToken(_accessToken, _expiresOn));
    }
}
