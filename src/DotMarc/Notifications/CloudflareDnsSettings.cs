namespace DotMarc.Notifications;

/// <summary>Singleton settings row for Cloudflare DNS push, same pattern as HaloPsaSettings — the
/// client secret lives in ISecretStore under SecretStoreKey, never on this entity.</summary>
public sealed class CloudflareDnsSettings
{
    public const string SecretStoreKey = "CloudflareDns.ClientSecret";

    public int Id { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
}
