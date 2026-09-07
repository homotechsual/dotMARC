namespace DotMarc.Notifications;

/// <summary>Singleton settings row for Google Cloud DNS push, same pattern as
/// CloudflareDnsSettings/AzureDnsSettings - the client secret lives in ISecretStore under
/// SecretStoreKey, never on this entity.</summary>
public sealed class GoogleCloudDnsSettings
{
    public const string SecretStoreKey = "GoogleCloudDns.ClientSecret";

    public int Id { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
}
