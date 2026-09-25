namespace DotMarc.Notifications;

/// <summary>Whether an alert type creates a HaloPSA ticket. A row with no <see cref="GroupId"/> is the global
/// setting for the type; a row with one is that group's override. No row means "use the next level down":
/// the group falls back to the global rule, which falls back to the registry default.</summary>
public sealed class AlertTicketRule
{
    public int Id { get; set; }
    public required string AlertType { get; set; }
    public int? GroupId { get; set; }
    public bool CreateTicket { get; set; }
}
