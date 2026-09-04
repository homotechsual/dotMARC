using Azure;
using Azure.Security.KeyVault.Secrets;

namespace DotMarc.Notifications;

/// <summary>Stores secrets in the Key Vault infra/main.bicep already provisions, one Key Vault
/// secret per store key — dots aren't valid in Key Vault secret names, so "HaloPsa.ClientSecret"
/// becomes "HaloPsa-ClientSecret" (matching the name already in production use). Selected instead
/// of DatabaseSecretStore when KeyVault:VaultUri is configured (see Program.cs) — requires the
/// container's managed identity to hold the write role infra/main.bicep grants only when
/// enableKeyVaultWrite is true. Values never touch Postgres.</summary>
public sealed class KeyVaultSecretStore : ISecretStore
{
    private readonly SecretClient _secretClient;

    public KeyVaultSecretStore(SecretClient secretClient) => _secretClient = secretClient;

    public async Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) =>
        await _secretClient.SetSecretAsync(ToKeyVaultName(key), value, cancellationToken).ConfigureAwait(false);

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = await _secretClient.GetSecretAsync(ToKeyVaultName(key), cancellationToken: cancellationToken).ConfigureAwait(false);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static string ToKeyVaultName(string key) => key.Replace('.', '-');
}
