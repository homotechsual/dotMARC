using DotMarc.Audit;
using DotMarc.Reporting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Saves a person's decisions about the senders on a domain, with an audit entry for each change. These are domain-level
/// decisions; baseline and group decisions are read here but written by their own screens.</summary>
public static class SenderDecisionService
{
    private const string UnreviewedLabel = "Unreviewed";
    private const string UniqueViolation = "23505";

    /// <summary>Sets or changes the domain's decision on a service or an IP. Does nothing, and writes no audit entry, when the
    /// status and note are already what is asked for. Two people deciding the same sender at once both succeed; the later write wins.</summary>
    public static async Task SetAsync(
        DotMarcDbContext context,
        AuditActor actor,
        int domainId,
        SenderDecisionKind kind,
        string key,
        SenderDecisionStatus status,
        string? note,
        CancellationToken cancellationToken = default)
    {
        key = key.Trim();
        if (key.Length is 0 or > SenderDecision.MaxKeyLength)
        {
            throw new ArgumentException($"The sender must be between 1 and {SenderDecision.MaxKeyLength} characters.", nameof(key));
        }

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > SenderDecision.MaxNoteLength })
        {
            throw new ArgumentException($"The note can be at most {SenderDecision.MaxNoteLength} characters.", nameof(note));
        }

        for (var attempt = 0; ; attempt++)
        {
            var domain = await context.Domains.SingleAsync(candidate => candidate.Id == domainId, cancellationToken).ConfigureAwait(false);
            var existing = await context.SenderDecisions.SingleOrDefaultAsync(
                decision => decision.Scope == SenderDecisionScope.Domain && decision.DomainId == domainId && decision.Kind == kind && decision.Key == key,
                cancellationToken).ConfigureAwait(false);

            var changes = new AuditChanges()
                .Field("Status", existing?.Status.Label() ?? UnreviewedLabel, status.Label())
                .Field("Note", existing?.Note, note);
            if (!changes.Any)
            {
                return;
            }

            if (existing is null)
            {
                existing = new SenderDecision { Scope = SenderDecisionScope.Domain, DomainId = domainId, Kind = kind, Key = key, DecidedBy = "", DecidedByName = "" };
                context.SenderDecisions.Add(existing);
            }

            existing.Status = status;
            existing.Note = note;
            existing.DecidedBy = Truncate(actor.ObjectId ?? actor.Email ?? actor.Name);
            existing.DecidedByName = Truncate(actor.Name);
            existing.DecidedUtc = DateTimeOffset.UtcNow;

            AuditLog.Record(context, actor, AuditActions.SenderDecisionChanged, AuditTarget.For(domain),
                $"Marked {key} as {status.Label()} for {domain.Name}", changes);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                // Someone else inserted this sender's row between our read and our write. Start again, now updating theirs.
                context.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Removes the domain's own decision, so the sender goes back to whatever it inherits (the group's, the baseline's, or
    /// Unreviewed). Does nothing when the domain has no decision of its own.</summary>
    public static async Task ResetAsync(
        DotMarcDbContext context,
        AuditActor actor,
        int domainId,
        SenderDecisionKind kind,
        string key,
        CancellationToken cancellationToken = default)
    {
        key = key.Trim();
        var existing = await context.SenderDecisions.SingleOrDefaultAsync(
            decision => decision.Scope == SenderDecisionScope.Domain && decision.DomainId == domainId && decision.Kind == kind && decision.Key == key,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var domain = await context.Domains.SingleAsync(candidate => candidate.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Status", existing.Status.Label(), UnreviewedLabel);
        context.SenderDecisions.Remove(existing);
        AuditLog.Record(context, actor, AuditActions.SenderDecisionChanged, AuditTarget.For(domain),
            $"Reset the decision for {key} on {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every decision that can apply to the domain: its own, those of the groups it is in, and the baseline.</summary>
    public static async Task<List<SenderDecision>> LoadForDomainAsync(DotMarcDbContext context, int domainId, CancellationToken cancellationToken = default)
    {
        var groupIds = await context.Domains.AsNoTracking()
            .Where(domain => domain.Id == domainId)
            .SelectMany(domain => domain.Groups.Select(group => group.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await context.SenderDecisions.AsNoTracking()
            .Where(decision => decision.Scope == SenderDecisionScope.Baseline
                || (decision.Scope == SenderDecisionScope.Domain && decision.DomainId == domainId)
                || (decision.Scope == SenderDecisionScope.Group && groupIds.Contains(decision.GroupId!.Value)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value) =>
        value.Length > UserDashboardPreference.MaxUserKeyLength ? value[..UserDashboardPreference.MaxUserKeyLength] : value;
}
