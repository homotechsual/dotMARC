namespace DotMarc.Notifications;

/// <summary>Stores and retrieves an encrypted secret by key. Two implementations:
/// DatabaseSecretStore (Postgres + Data Protection, the default/fallback) and KeyVaultSecretStore
/// (Azure, opt-in) — selected in Program.cs on whether KeyVault:VaultUri is configured. Shared
/// across every integration that needs a runtime-editable secret (HaloPSA, Cloudflare DNS push,
/// Azure DNS push) rather than one near-identical store per integration. Keys are dot-namespaced
/// business names (e.g. "HaloPsa.ClientSecret") defined as a SecretStoreKey constant on the
/// settings entity the secret belongs to.</summary>
public interface ISecretStore
{
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
}
