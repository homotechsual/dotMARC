using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Azure DNS via a delegated Entra ID authorization-code
/// exchange — the push only succeeds if the SIGNED-IN USER's own Azure RBAC grants them write
/// access on the target zone; dotMARC never holds a standing grant of its own. Same "nothing about
/// the end-user's push-time token is ever persisted" contract as CloudflareDnsPushProvider. The
/// app's own OAuth client credentials are DB-backed (AzureDnsSettings/ISecretStore), read fresh
/// per call.</summary>
public sealed class AzureDnsPushProvider : IDnsPushProvider
{
    private const string Scope = "https://management.azure.com/user_impersonation";

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;

    public AzureDnsPushProvider(IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
    }

    public string ProviderKey => "azure-dns";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(settings.TenantId) && !string.IsNullOrEmpty(settings.ClientId) && settings.ClientSecretConfigured;
    }

    public async Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = $"{Scope} openid",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/authorize?" +
            string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var clientSecret = await _secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.TenantId) || string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Azure DNS push is not configured for this deployment.");
        }

        var confidentialClient = ConfidentialClientApplicationBuilder.Create(settings.ClientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{settings.TenantId}")
            .WithRedirectUri(redirectUri)
            .Build();

        AuthenticationResult authResult;
        try
        {
            authResult = await confidentialClient
                .AcquireTokenByAuthorizationCode([Scope], code)
                .WithPkceCodeVerifier(codeVerifier)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the authorization code exchange: {ex.Message}");
        }

        var armClient = new ArmClient(new FixedTokenCredential(authResult.AccessToken, authResult.ExpiresOn));

        foreach (var change in changes)
        {
            var result = await PushOneChangeAsync(armClient, change, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != DnsPushOutcome.Pushed)
            {
                return result;
            }
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
    }

    private static async Task<DnsPushResult> PushOneChangeAsync(ArmClient armClient, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var zoneName = change.ZoneName;
        var zone = await FindZoneAsync(armClient, zoneName, cancellationToken).ConfigureAwait(false);
        if (zone is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound,
                $"Couldn't find {zoneName} in any subscription you authorized — check you have DNS Zone Contributor rights on it.");
        }

        return await PushRecordAsync(zone, zoneName, change, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AzureDnsSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await AzureDnsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
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
                var cnameRecords = zone.GetDnsCnameRecords();
                if (change.Kind == DnsRecordChangeKind.Create
                    && (await cnameRecords.ExistsAsync(relativeName, cancellationToken).ConfigureAwait(false)).Value)
                {
                    return new DnsPushResult(DnsPushOutcome.ProviderError,
                        $"A DNS record already exists at {change.Name} — remove it or update it manually rather than risk overwriting it.");
                }

                var data = new DnsCnameRecordData { TtlInSeconds = 3600, Cname = change.DesiredValue };
                await cnameRecords.CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var txtRecords = zone.GetDnsTxtRecords();
                if (change.Kind == DnsRecordChangeKind.Create
                    && (await txtRecords.ExistsAsync(relativeName, cancellationToken).ConfigureAwait(false)).Value)
                {
                    return new DnsPushResult(DnsPushOutcome.ProviderError,
                        $"A DNS record already exists at {change.Name} — remove it or update it manually rather than risk overwriting it.");
                }

                var data = new DnsTxtRecordData { TtlInSeconds = 3600 };
                data.DnsTxtRecords.Add(new DnsTxtRecordInfo { Values = { change.DesiredValue } });
                await txtRecords.CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the record push: {ex.Message}");
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
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
