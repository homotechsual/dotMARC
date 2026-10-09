using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa;

/// <summary>Checks dotMARC's open tickets in each ready PSA: a ticket closed there resolves its alert, a deleted one
/// stops being checked, and a resolved alert's ticket that failed to close is closed again. ConnectWise and Autotask
/// rely on this; for HaloPSA it backs up the webhook.
///
/// Each ticket is handled on its own, so one that can't be read or closed never stops the others. A PSA where every
/// ticket failed in a cycle is taken to be down and is asked less often (doubling up to an hour), logged once per step
/// rather than every cycle.</summary>
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

        // Least recently checked first, so a ticket that keeps failing can't keep the others waiting.
        var openTickets = await context.AlertTickets
            .Where(ticket => ticket.Psa == provider.Kind && ticket.IsOpen)
            .OrderBy(ticket => ticket.LastCheckedUtc ?? ticket.CreatedUtc)
            .Join(context.AlertEvents, ticket => ticket.AlertEventId, alert => alert.Id, (ticket, alert) => new { Ticket = ticket, alert.IsResolved })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Exception? lastFailure = null;
        var failures = 0;
        var handled = 0;
        foreach (var open in openTickets)
        {
            // Resolving one alert closes its other tickets in this PSA too, so skip any already handled this cycle.
            if (!open.Ticket.IsOpen)
            {
                continue;
            }

            try
            {
                if (await CheckTicketAsync(context, provider, open.Ticket, open.IsResolved, cancellationToken).ConfigureAwait(false))
                {
                    handled++;
                }
                else
                {
                    failures++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                lastFailure = exception;
                logger.LogWarning(exception, "Checking {Psa} ticket {TicketId} failed. dotMARC will try again.", provider.Kind.DisplayName(), open.Ticket.TicketId);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Every ticket failing looks like the PSA being down, so back off. Some working means only those tickets are
        // the problem, and they're retried next cycle with the rest.
        if (failures > 0 && handled == 0)
        {
            throw new HttpRequestException($"Every {provider.Kind.DisplayName()} ticket check failed this cycle.", lastFailure);
        }
    }

    /// <summary>Checks one ticket. False when its close was retried and failed again (already logged).</summary>
    private async Task<bool> CheckTicketAsync(DotMarcDbContext context, IPsaProvider provider, AlertTicket ticket, bool alertResolved, CancellationToken cancellationToken)
    {
        var state = await provider.GetTicketStateAsync(context, ticket.TicketId, cancellationToken).ConfigureAwait(false);
        ticket.LastCheckedUtc = timeProvider.GetUtcNow();

        if (alertResolved)
        {
            // An earlier close failed. If a tech has since closed or deleted the ticket there's nothing left to do;
            // retrying would fail for ever on a deleted ticket, and add a note each time to a closed one.
            if (state is PsaTicketState.Closed or PsaTicketState.Missing)
            {
                ticket.IsOpen = false;
                return true;
            }

            return (await ticketService.CloseTicketAsync(context, ticket, cancellationToken).ConfigureAwait(false)).Failed == 0;
        }

        switch (state)
        {
            case PsaTicketState.Closed:
                await PsaTicketClosure.ResolveFromTicketAsync(context, ticketService, provider.Kind, ticket.TicketId, cancellationToken).ConfigureAwait(false);
                break;
            case PsaTicketState.Missing:
                ticket.IsOpen = false;
                logger.LogInformation("{Psa} ticket {TicketId} no longer exists, so dotMARC stopped checking it.", provider.Kind.DisplayName(), ticket.TicketId);
                break;
        }

        return true;
    }
}
