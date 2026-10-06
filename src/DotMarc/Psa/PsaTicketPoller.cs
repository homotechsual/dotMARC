using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa;

/// <summary>Checks dotMARC's open tickets in each ready PSA: a ticket closed there resolves its alert, a deleted one
/// stops being checked, and a resolved alert's ticket that failed to close is closed again. ConnectWise and Autotask
/// rely on this; for HaloPSA it backs up the webhook. A PSA that keeps failing is asked less often (doubling up to an
/// hour) and logged once per step rather than every cycle.</summary>
public sealed class PsaTicketPoller(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    IEnumerable<IPsaProvider> providers,
    PsaTicketService ticketService,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<PsaTicketPoller> logger) : BackgroundService
{
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<IPsaProvider> _providers = providers.ToList();
    private readonly Dictionary<PsaKind, (int Failures, DateTimeOffset NextAttemptUtc)> _backoff = [];

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Psa:PollIntervalMinutes", 5)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Checking PSA tickets failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            var now = timeProvider.GetUtcNow();
            if (_backoff.TryGetValue(provider.Kind, out var waiting) && now < waiting.NextAttemptUtc)
            {
                continue;
            }

            try
            {
                await PollProviderAsync(provider, cancellationToken).ConfigureAwait(false);
                _backoff.Remove(provider.Kind);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failures = _backoff.TryGetValue(provider.Kind, out var previous) ? previous.Failures + 1 : 1;
                var wait = TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, Interval.Ticks * (1L << Math.Min(failures - 1, 10))));
                _backoff[provider.Kind] = (failures, now + wait);
                logger.LogWarning(exception, "Checking {Psa} tickets failed ({Failures} in a row). Trying again in {Minutes} minutes.", provider.Kind.DisplayName(), failures, wait.TotalMinutes);
            }
        }
    }

    private async Task PollProviderAsync(IPsaProvider provider, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!(await provider.GetReadinessAsync(context, cancellationToken).ConfigureAwait(false)).IsReady)
        {
            return;
        }

        var openTickets = await context.AlertTickets
            .Where(ticket => ticket.Psa == provider.Kind && ticket.IsOpen)
            .Join(context.AlertEvents, ticket => ticket.AlertEventId, alert => alert.Id, (ticket, alert) => new { Ticket = ticket, alert.IsResolved })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var open in openTickets)
        {
            // Resolving one alert closes its other tickets in this PSA too, so skip any already handled this cycle.
            if (!open.Ticket.IsOpen)
            {
                continue;
            }

            if (open.IsResolved)
            {
                var retried = await ticketService.CloseTicketAsync(context, open.Ticket, cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (retried.Failed > 0)
                {
                    throw new HttpRequestException($"Closing {provider.Kind.DisplayName()} ticket {open.Ticket.TicketId} failed again.");
                }

                continue;
            }

            switch (await provider.GetTicketStateAsync(context, open.Ticket.TicketId, cancellationToken).ConfigureAwait(false))
            {
                case PsaTicketState.Closed:
                    await PsaTicketClosure.ResolveFromTicketAsync(context, ticketService, provider.Kind, open.Ticket.TicketId, cancellationToken).ConfigureAwait(false);
                    break;
                case PsaTicketState.Missing:
                    open.Ticket.IsOpen = false;
                    logger.LogInformation("{Psa} ticket {TicketId} no longer exists, so dotMARC stopped checking it.", provider.Kind.DisplayName(), open.Ticket.TicketId);
                    break;
                default:
                    open.Ticket.LastCheckedUtc = timeProvider.GetUtcNow();
                    break;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
