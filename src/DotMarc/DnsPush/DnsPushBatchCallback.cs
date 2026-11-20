using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Reporting;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.DnsPush;

/// <summary>What happens when the provider sends a person back after they have signed in for a batch. It returns the flag the popup shows:
/// "batch-started" when the pushes are under way, "cancelled" when the person backed out at the provider (the batch can be tried again),
/// "error" when the provider rejected the sign-in, and "invalid" for everything else, deliberately without saying which, since the person
/// can't act on the difference.</summary>
public sealed class DnsPushBatchCallback(
    IEnumerable<IDnsChangeBuilder> changeBuilders, IAuthorizationService authorizationService, DnsPushBatchQueue queue, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<string> HandleAsync(
        DotMarcDbContext context, IDnsPushProvider pushProvider, DnsPushState state, string? code, string? error, string redirectUri,
        ClaimsPrincipal user, AuditActor actor, CancellationToken cancellationToken)
    {
        if (state.BatchId is not { } batchId || changeBuilders.Find(state.PushTarget) is not { } builder)
        {
            return "invalid";
        }

        // The signed state proves the redirect was legitimate, not that whoever lands here still holds the permission the target needs.
        if (!(await authorizationService.AuthorizeAsync(user, builder.RequiredPolicy).ConfigureAwait(false)).Succeeded)
        {
            return "invalid";
        }

        var now = _timeProvider.GetUtcNow();
        var check = await DnsPushBatchGate.CheckAsync(context, batchId, DashboardPreferencesStore.UserKeyOf(actor), pushProvider.ProviderKey, now, cancellationToken).ConfigureAwait(false);
        if (check.Batch is null || !string.Equals(check.Batch.Target, state.PushTarget, StringComparison.Ordinal))
        {
            return "invalid";
        }

        // Backing out at the provider claims nothing, so the review can be signed in to again while it is still open.
        if (error is not null || string.IsNullOrEmpty(code))
        {
            return "cancelled";
        }

        // Claimed before the code is spent, so a repeated callback finds the batch already taken and spends nothing.
        if (!await DnsPushBatchGate.ClaimAsync(context, batchId, now, cancellationToken).ConfigureAwait(false))
        {
            return "invalid";
        }

        var exchange = await pushProvider.ExchangeAsync(code, state.CodeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        if (exchange.Session is not { } session)
        {
            await DnsPushBatchGate.AbandonAsync(context, batchId, now, cancellationToken).ConfigureAwait(false);
            return "error";
        }

        queue.Enqueue(new DnsPushBatchWork(batchId, pushProvider.ProviderKey, session, actor));
        return "batch-started";
    }
}
