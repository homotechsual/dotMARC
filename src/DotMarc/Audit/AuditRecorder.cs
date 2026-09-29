using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Audit;

/// <summary>Saves an audit entry on its own, for things that aren't a database change a service makes: sign-ins,
/// page views, DNS pushes and Halo actions. Best-effort by design: a failure is logged as a warning and never
/// reaches the person, so a database hiccup can't block a sign-in or a page.</summary>
public sealed class AuditRecorder(IDbContextFactory<DotMarcDbContext> dbContextFactory, ILogger<AuditRecorder> logger)
{
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            context.AuditEntries.Add(entry);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Couldn't record the audit entry {Action} for {ActorName}", entry.Action, entry.ActorName);
        }
    }
}
