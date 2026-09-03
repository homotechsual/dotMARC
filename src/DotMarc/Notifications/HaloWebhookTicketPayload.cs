// src/DotMarc/Notifications/HaloWebhookTicketPayload.cs
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

public sealed record HaloWebhookTicketPayload(
    [property: JsonPropertyName("ticket_id")] int TicketId,
    [property: JsonPropertyName("status_id")] int StatusId);
