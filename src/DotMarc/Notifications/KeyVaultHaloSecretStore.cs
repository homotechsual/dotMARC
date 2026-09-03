using Azure;
using Azure.Security.KeyVault.Secrets;

namespace DotMarc.Notifications;

/// <summary>Stores the HaloPSA client secret in the Key Vault infra/main.bicep already
/// provisions, under a fixed secret name. Selected instead of DatabaseHaloSecretStore when
/// KeyVault:VaultUri is configured (see Program.cs) — requires the container's managed identity
/// to hold the write role infra/main.bicep grants only when enableHaloPsaKeyVaultWrite is true.
/// The value never touches Postgres.</summary>
public sealed class KeyVaultHaloSecretStore : IHaloSecretStore
{
    private const string SecretName = "HaloPsa-ClientSecret";
    private readonly SecretClient _secretClient;

    public KeyVaultHaloSecretStore(SecretClient secretClient) => _secretClient = secretClient;

    public async Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default) =>
        await _secretClient.SetSecretAsync(SecretName, clientSecret, cancellationToken).ConfigureAwait(false);

    public async Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = await _secretClient.GetSecretAsync(SecretName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
