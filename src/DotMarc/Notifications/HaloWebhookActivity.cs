using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

public enum HaloWebhookDelivery
{
    /// <summary>The right secret, and a status that matches the configured closed status.</summary>
    ClosedStatus,

    /// <summary>The right secret, but some other status change, which is ignored.</summary>
    OtherStatus,

    /// <summary>The right secret, but a body dotMARC couldn't read (it expects JSON with
    /// <c>ticket_id</c> and <c>status_id</c>).</summary>
    Unreadable,

    /// <summary>A request reached the webhook path with a secret that doesn't match.</summary>
    WrongSecret
}

public sealed record HaloWebhookReceipt(DateTimeOffset ReceivedUtc, HaloWebhookDelivery Delivery, int? TicketId, int? StatusId, bool ResolvedAnAlert);

/// <summary>A short, in-memory record of recent calls to the HaloPSA webhook. The endpoint used to
/// leave no trace, so "did Halo actually call back?" could not be answered from inside the app. It
/// records only ticket and status numbers, never a secret or a request body, and forgets everything
/// on restart.</summary>
public sealed class HaloWebhookActivity(ILogger<HaloWebhookActivity> logger)
{
    private const int Capacity = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly Queue<HaloWebhookReceipt> _receipts = new();

    public void Record(HaloWebhookDelivery delivery, int? ticketId = null, int? statusId = null, bool resolvedAnAlert = false)
    {
        lock (_gate)
        {
            _receipts.Enqueue(new HaloWebhookReceipt(DateTimeOffset.UtcNow, delivery, ticketId, statusId, resolvedAnAlert));
            while (_receipts.Count > Capacity)
            {
                _receipts.Dequeue();
            }
        }

        if (delivery == HaloWebhookDelivery.WrongSecret)
        {
            logger.LogWarning("A request reached the HaloPSA webhook with the wrong secret. If HaloPSA sent it, re-copy the webhook URL from Alert settings after saving.");
        }
        else
        {
            logger.LogInformation("HaloPSA webhook received: {Delivery}, ticket {TicketId}, status {StatusId}, resolved an alert: {ResolvedAnAlert}.", delivery, ticketId, statusId, resolvedAnAlert);
        }
    }

    /// <summary>The most recent receipts, newest first.</summary>
    public IReadOnlyList<HaloWebhookReceipt> Recent(int count)
    {
        lock (_gate)
        {
            return _receipts.Reverse().Take(count).ToList();
        }
    }

    /// <summary>Waits for the first receipt at or after <paramref name="since"/> that
    /// <paramref name="match"/> accepts, or returns null once <paramref name="timeout"/> passes.</summary>
    public async Task<HaloWebhookReceipt?> WaitForAsync(Func<HaloWebhookReceipt, bool> match, DateTimeOffset since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            HaloWebhookReceipt? found;
            lock (_gate)
            {
                found = _receipts.FirstOrDefault(receipt => receipt.ReceivedUtc >= since && match(receipt));
            }

            if (found is not null)
            {
                return found;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
