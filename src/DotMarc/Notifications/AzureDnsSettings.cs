namespace DotMarc.Notifications;

/// <summary>Singleton settings row for Azure DNS push, same pattern as HaloPsaSettings — the
/// client secret lives in ISecretStore under SecretStoreKey, never on this entity.</summary>
public sealed class AzureDnsSettings
{
    public const string SecretStoreKey = "AzureDns.ClientSecret";

    public int Id { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
}
