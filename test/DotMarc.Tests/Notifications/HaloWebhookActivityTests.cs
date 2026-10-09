using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

public class HaloWebhookActivityTests
{
    private static HaloWebhookActivity CreateActivity() => new(NullLogger<HaloWebhookActivity>.Instance);

    [Fact]
    public void Recent_ReturnsTheNewestFirst_AndKeepsOnlyTheLatestHundred()
    {
        var activity = CreateActivity();
        for (var ticket = 1; ticket <= 150; ticket++)
        {
            activity.Record(HaloWebhookDelivery.ClosedStatus, ticket, 9);
        }

        var recent = activity.Recent(500);

        Assert.Equal(100, recent.Count);
        Assert.Equal(150, recent[0].TicketId);
        Assert.Equal(51, recent[^1].TicketId);
    }

    [Fact]
    public async Task WaitFor_ReturnsAReceiptThatArrivesWhileWaiting()
    {
        var activity = CreateActivity();
        var since = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            activity.Record(HaloWebhookDelivery.ClosedStatus, 42, 9);
        });

        var receipt = await activity.WaitForAsync(r => r.TicketId == 42, since, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(receipt);
        Assert.Equal(HaloWebhookDelivery.ClosedStatus, receipt.Delivery);
    }

    [Fact]
    public async Task WaitFor_ReturnsNull_WhenNothingMatchesBeforeTheTimeout()
    {
        var activity = CreateActivity();
        activity.Record(HaloWebhookDelivery.ClosedStatus, 7, 9);

        var receipt = await activity.WaitForAsync(r => r.TicketId == 42, DateTimeOffset.MinValue, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(receipt);
    }

    [Fact]
    public async Task WaitFor_IgnoresReceiptsFromBeforeTheStartTime()
    {
        var activity = CreateActivity();
        activity.Record(HaloWebhookDelivery.ClosedStatus, 42, 9);
        await Task.Delay(30);

        var receipt = await activity.WaitForAsync(r => r.TicketId == 42, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(receipt);
    }
}
