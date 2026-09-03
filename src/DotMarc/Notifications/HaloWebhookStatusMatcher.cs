// src/DotMarc/Notifications/HaloWebhookStatusMatcher.cs
namespace DotMarc.Notifications;

public static class HaloWebhookStatusMatcher
{
    public static bool IsClosedStatus(HaloWebhookTicketPayload payload, HaloPsaSettings settings) =>
        settings.ClosedStatusId is { } closedStatusId && payload.StatusId == closedStatusId;
}
