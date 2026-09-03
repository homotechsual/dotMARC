// test/DotMarc.Tests/Notifications/HaloWebhookStatusMatcherTests.cs
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloWebhookStatusMatcherTests
{
    [Fact]
    public void IsClosedStatus_ReturnsTrue_WhenTheStatusIdMatchesTheConfiguredClosedStatus()
    {
        var payload = new HaloWebhookTicketPayload(TicketId: 4242, StatusId: 9);
        var settings = new HaloPsaSettings { ClosedStatusId = 9 };

        Assert.True(HaloWebhookStatusMatcher.IsClosedStatus(payload, settings));
    }

    [Fact]
    public void IsClosedStatus_ReturnsFalse_ForADifferentStatus()
    {
        var payload = new HaloWebhookTicketPayload(TicketId: 4242, StatusId: 3);
        var settings = new HaloPsaSettings { ClosedStatusId = 9 };

        Assert.False(HaloWebhookStatusMatcher.IsClosedStatus(payload, settings));
    }

    [Fact]
    public void IsClosedStatus_ReturnsFalse_WhenNoClosedStatusIsConfigured()
    {
        var payload = new HaloWebhookTicketPayload(TicketId: 4242, StatusId: 9);
        var settings = new HaloPsaSettings { ClosedStatusId = null };

        Assert.False(HaloWebhookStatusMatcher.IsClosedStatus(payload, settings));
    }
}
