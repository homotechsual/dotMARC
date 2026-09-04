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

        if (change.Kind == DnsRecordChangeKind.Replace)
        {
            return await ReplaceRecordAsync(zone, relativeName, change, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>Deletes whatever record of change.ExistingRecordType currently exists at
    /// relativeName, then creates a change.RecordType record with change.DesiredValue in its
    /// place. DNS doesn't allow a CNAME to coexist with any other record type at the same name, so
    /// this is the only way to convert a third-party CNAME delegation into a record dotMARC
    /// manages directly. If the delete succeeds but the create then fails, the name is left with
    /// neither record; that failure is reported explicitly rather than attempting an automatic
    /// rollback (see the design spec's failure-handling decision).</summary>
    private static async Task<DnsPushResult> ReplaceRecordAsync(DnsZoneResource zone, string relativeName, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var existingType = change.ExistingRecordType ?? change.RecordType;

        if (!string.Equals(existingType, "CNAME", StringComparison.OrdinalIgnoreCase))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Don't know how to delete an existing {existingType} record — only CNAME is supported for a replace. Nothing was changed.");
        }
        if (!string.Equals(change.RecordType, "TXT", StringComparison.OrdinalIgnoreCase))
        {
            // Mirrors the guard above: Replace is only ever built (in Program.cs) as CNAME-to-TXT
            // today. Guarding this side too means a future record type added without updating this
            // method fails loudly here instead of silently creating a TXT record under the wrong
            // type's name, then reporting the wrong type back in the error message.
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Don't know how to create a {change.RecordType} record for a replace — only TXT is supported. Nothing was changed.");
        }

        try
        {
            var existingRecord = await zone.GetDnsCnameRecords().GetAsync(relativeName, cancellationToken).ConfigureAwait(false);
            await existingRecord.Value.DeleteAsync(WaitUntil.Completed, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Deliberately ProviderError, not ZoneNotFound — the zone WAS found; this specific
            // record just isn't there anymore (most likely removed since this page loaded, a
            // benign race a retry would clear). Reusing ZoneNotFound's "check the account you
            // authorized" message here would send the admin chasing a permissions problem that
            // doesn't exist.
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"The {existingType} record at {change.Name} no longer exists at Azure DNS — it may have been removed since this page loaded. Nothing was changed; try again.");
        }
        catch (RequestFailedException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected deleting the existing {existingType} record: {ex.Message} — nothing was changed.");
        }

        // The delete above succeeded — from here on, any failure means the name now has NO
        // record at all, which is why the remaining failure path returns ReplaceFailedAfterDelete
        // instead of the generic ProviderError.
        try
        {
            var txtRecords = zone.GetDnsTxtRecords();
            var data = new DnsTxtRecordData { TtlInSeconds = 3600 };
            data.DnsTxtRecords.Add(new DnsTxtRecordInfo { Values = { change.DesiredValue } });
            await txtRecords.CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ReplaceFailedAfterDelete, $"The old {existingType} record at {change.Name} was deleted, but creating the new {change.RecordType} record failed: {ex.Message} — this name now has no record and needs manual attention.");
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
