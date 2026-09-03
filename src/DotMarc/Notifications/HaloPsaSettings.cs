namespace DotMarc.Notifications;

/// <summary>Singleton settings row for the HaloPSA PSA integration — same "exactly one row,
/// seeded via migration HasData" pattern as NotificationSettings. ProtectedClientSecret is
/// written and read only by DatabaseHaloSecretStore (see IHaloSecretStore); every other reader
/// of this entity should treat ClientSecretConfigured as the only signal about the secret's
/// presence, never the protected value itself.</summary>
public sealed class HaloPsaSettings
{
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
    public string? ProtectedClientSecret { get; set; }
}
