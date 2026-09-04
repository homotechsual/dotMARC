namespace DotMarc.Notifications;

/// <summary>Singleton settings row for the HaloPSA PSA integration — same "exactly one row,
/// seeded via migration HasData" pattern as NotificationSettings. The client secret itself lives
/// in ISecretStore under SecretStoreKey, never on this entity — every reader of this entity should
/// treat ClientSecretConfigured as the only signal about the secret's presence.</summary>
public sealed class HaloPsaSettings
{
    public const string SecretStoreKey = "HaloPsa.ClientSecret";

    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? AccountName { get; set; }
    public string? AuthServerUrl { get; set; }
    public string? ResourceServerUrl { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
    public int? TicketTypeId { get; set; }
    public int? DefaultPriorityId { get; set; }
    public int? ClosedStatusId { get; set; }
    public string? WebhookSecret { get; set; }
}
