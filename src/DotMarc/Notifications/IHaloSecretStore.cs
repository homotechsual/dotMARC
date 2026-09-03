namespace DotMarc.Notifications;

/// <summary>Stores and retrieves the HaloPSA API client secret. Two implementations: this one
/// (Postgres + Data Protection, the default/fallback) and KeyVaultHaloSecretStore (Azure,
/// opt-in) — selected in Program.cs on whether KeyVault:VaultUri is configured. Never exposes the
/// value through HaloPsaSettings itself.</summary>
public interface IHaloSecretStore
{
    Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default);
    Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default);
}
