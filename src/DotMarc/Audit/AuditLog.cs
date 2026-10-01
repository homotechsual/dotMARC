using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Audit;

/// <summary>Creates and records audit entries. Services call <see cref="Record"/> before their own save, so a
/// change and its entry are saved together or not at all.</summary>
public static class AuditLog
{
    public static AuditEntry Create(AuditActor actor, AuditEntryKind kind, string action, AuditTarget? target, string summary, IReadOnlyList<AuditFieldChange>? changes = null) => new()
    {
        OccurredUtc = DateTimeOffset.UtcNow,
        Kind = kind,
        ActorKind = actor.Kind,
        ActorObjectId = actor.ObjectId,
        ActorEmail = actor.Email,
        ActorName = actor.Name,
        Action = action,
        TargetType = target?.Type,
        TargetId = target?.Id,
        TargetName = target?.Name,
        Summary = summary,
        Changes = changes?.ToList() ?? []
    };

    /// <summary>Adds a change entry to the context. The caller's own SaveChangesAsync saves it with the change.</summary>
    public static void Record(DotMarcDbContext context, AuditActor actor, string action, AuditTarget? target, string summary, AuditChanges? changes = null) =>
        context.AuditEntries.Add(Create(actor, AuditEntryKind.Change, action, target, summary, changes?.Items));

    /// <summary>For a change whose entry needs an id the database assigns, such as a newly added domain: saves the
    /// change, lets <paramref name="recordEntries"/> call <see cref="Record"/> now the id is known, and saves again,
    /// all in one transaction so neither is kept without the other. An exception from the first save (such as a
    /// unique-name race) propagates to the caller unchanged. Inside a transaction the caller already opened, it joins
    /// that one instead.</summary>
    public static async Task SaveAndRecordAsync(DotMarcDbContext context, Action recordEntries, CancellationToken cancellationToken)
    {
        // Inside a caller's transaction (a bulk import), join it: the caller commits or rolls back everything.
        if (context.Database.CurrentTransaction is { } callersTransaction)
        {
            // A failed statement aborts the whole Postgres transaction. A savepoint lets a unique-name race (which the
            // services treat as "already exists") be undone on its own, so the caller's transaction carries on.
            const string savepoint = "before_audited_save";
            await callersTransaction.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
            {
                await callersTransaction.RollbackToSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
                // Stop tracking what failed, or the caller's next save would try it again. If the provider didn't say
                // which entity failed, drop every pending insert: the callers here have only the one.
                var failed = exception.Entries.Count > 0
                    ? exception.Entries
                    : context.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added).ToList();
                foreach (var entry in failed)
                {
                    entry.State = EntityState.Detached;
                }

                throw;
            }

            recordEntries();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        recordEntries();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
