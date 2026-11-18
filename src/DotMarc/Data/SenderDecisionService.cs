using DotMarc.Audit;
using DotMarc.Reporting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Saves decisions about senders, with an audit entry for each change. A decision is made for one domain, for the
/// domains of one group, or for every domain (the baseline). The resolver in <c>DotMarc.Reporting</c> decides which applies.</summary>
public static class SenderDecisionService
{
    private const string UnreviewedLabel = "Unreviewed";
    private const string UniqueViolation = "23505";

    /// <summary>Where a decision lives, how it reads in the audit log, and which audit entry it belongs to.</summary>
    private sealed record Owner(SenderDecisionScope Scope, int? GroupId, int? DomainId, AuditTarget Target, string SetPhrase, string ResetPhrase);

    // ---- A domain's own decisions, on a service or an IP ----

    /// <summary>Sets or changes the domain's decision on a service or an IP. Does nothing, and writes no audit entry, when the
    /// status and note are already what is asked for. Two people deciding the same sender at once both succeed; the later write wins.</summary>
    public static Task SetAsync(
        DotMarcDbContext context, AuditActor actor, int domainId, SenderDecisionKind kind, string key, SenderDecisionStatus status, string? note,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(context, actor, SenderDecisionScope.Domain, null, domainId, kind, key, status, note, cancellationToken);

    /// <summary>Removes the domain's own decision, so the sender goes back to whatever it inherits (the group's, the baseline's, or
    /// Unreviewed). Does nothing when the domain has no decision of its own.</summary>
    public static Task ResetAsync(
        DotMarcDbContext context, AuditActor actor, int domainId, SenderDecisionKind kind, string key, CancellationToken cancellationToken = default) =>
        RemoveAsync(context, actor, SenderDecisionScope.Domain, null, domainId, kind, key, cancellationToken);

    // ---- A group's defaults and the baseline, on a service only ----

    public static Task SetForGroupAsync(
        DotMarcDbContext context, AuditActor actor, int groupId, string serviceName, SenderDecisionStatus status, string? note,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(context, actor, SenderDecisionScope.Group, groupId, null, SenderDecisionKind.Service, RequireCatalogueName(serviceName), status, note, cancellationToken);

    public static Task ResetForGroupAsync(
        DotMarcDbContext context, AuditActor actor, int groupId, string serviceName, CancellationToken cancellationToken = default) =>
        RemoveAsync(context, actor, SenderDecisionScope.Group, groupId, null, SenderDecisionKind.Service, serviceName, cancellationToken);

    public static Task SetBaselineAsync(
        DotMarcDbContext context, AuditActor actor, string serviceName, SenderDecisionStatus status, string? note, CancellationToken cancellationToken = default) =>
        UpsertAsync(context, actor, SenderDecisionScope.Baseline, null, null, SenderDecisionKind.Service, RequireCatalogueName(serviceName), status, note, cancellationToken);

    public static Task ResetBaselineAsync(
        DotMarcDbContext context, AuditActor actor, string serviceName, CancellationToken cancellationToken = default) =>
        RemoveAsync(context, actor, SenderDecisionScope.Baseline, null, null, SenderDecisionKind.Service, serviceName, cancellationToken);

    // ---- Reading ----

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

    public static async Task<List<SenderDecision>> LoadBaselineAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await context.SenderDecisions.AsNoTracking()
            .Where(decision => decision.Scope == SenderDecisionScope.Baseline)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The group's own decisions and the baseline, which is what the group's defaults screen shows.</summary>
    public static async Task<List<SenderDecision>> LoadForGroupAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        await context.SenderDecisions.AsNoTracking()
            .Where(decision => decision.Scope == SenderDecisionScope.Baseline
                || (decision.Scope == SenderDecisionScope.Group && decision.GroupId == groupId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    // ---- Shared ----

    private static async Task UpsertAsync(
        DotMarcDbContext context, AuditActor actor, SenderDecisionScope scope, int? groupId, int? domainId, SenderDecisionKind kind, string key,
        SenderDecisionStatus status, string? note, CancellationToken cancellationToken)
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
            var owner = await LoadOwnerAsync(context, scope, groupId, domainId, cancellationToken).ConfigureAwait(false);
            var existing = await FindAsync(context, scope, groupId, domainId, kind, key, cancellationToken).ConfigureAwait(false);

            var changes = new AuditChanges()
                .Field("Status", existing?.Status.Label() ?? UnreviewedLabel, status.Label())
                .Field("Note", existing?.Note, note);
            if (!changes.Any)
            {
                return;
            }

            if (existing is null)
            {
                existing = new SenderDecision { Scope = scope, GroupId = groupId, DomainId = domainId, Kind = kind, Key = key, DecidedBy = "", DecidedByName = "" };
                context.SenderDecisions.Add(existing);
            }

            existing.Status = status;
            existing.Note = note;
            existing.DecidedBy = Truncate(actor.ObjectId ?? actor.Email ?? actor.Name);
            existing.DecidedByName = Truncate(actor.Name);
            existing.DecidedUtc = DateTimeOffset.UtcNow;

            AuditLog.Record(context, actor, AuditActions.SenderDecisionChanged, owner.Target, $"Marked {key} as {status.Label()} {owner.SetPhrase}", changes);
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

    private static async Task RemoveAsync(
        DotMarcDbContext context, AuditActor actor, SenderDecisionScope scope, int? groupId, int? domainId, SenderDecisionKind kind, string key,
        CancellationToken cancellationToken)
    {
        key = key.Trim();
        var existing = await FindAsync(context, scope, groupId, domainId, kind, key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var owner = await LoadOwnerAsync(context, scope, groupId, domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Status", existing.Status.Label(), UnreviewedLabel);
        context.SenderDecisions.Remove(existing);
        AuditLog.Record(context, actor, AuditActions.SenderDecisionChanged, owner.Target, $"Reset the decision for {key} {owner.ResetPhrase}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<SenderDecision?> FindAsync(
        DotMarcDbContext context, SenderDecisionScope scope, int? groupId, int? domainId, SenderDecisionKind kind, string key, CancellationToken cancellationToken) =>
        context.SenderDecisions.SingleOrDefaultAsync(
            decision => decision.Scope == scope && decision.GroupId == groupId && decision.DomainId == domainId && decision.Kind == kind && decision.Key == key,
            cancellationToken);

    private static async Task<Owner> LoadOwnerAsync(
        DotMarcDbContext context, SenderDecisionScope scope, int? groupId, int? domainId, CancellationToken cancellationToken)
    {
        switch (scope)
        {
            case SenderDecisionScope.Domain:
                var domain = await context.Domains.SingleAsync(candidate => candidate.Id == domainId, cancellationToken).ConfigureAwait(false);
                return new Owner(scope, null, domain.Id, AuditTarget.For(domain), $"for {domain.Name}", $"on {domain.Name}");
            case SenderDecisionScope.Group:
                var group = await context.Groups.SingleAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
                return new Owner(scope, group.Id, null, AuditTarget.For(group), $"in {group.Name}'s sender defaults", $"in {group.Name}'s sender defaults");
            default:
                return new Owner(scope, null, null, AuditTarget.Settings("Sender baseline"), "in the sender baseline", "in the sender baseline");
        }
    }

    /// <summary>New group and baseline decisions must name a service the catalogue knows. A stored decision for a name that has
    /// since left the catalogue stays and can still be reset, since resetting does not check the name.</summary>
    private static string RequireCatalogueName(string serviceName)
    {
        var name = serviceName.Trim();
        return SenderServiceCatalog.All.Any(service => service.Name == name)
            ? name
            : throw new ArgumentException($"'{serviceName}' is not a sending service dotMARC knows.", nameof(serviceName));
    }

    private static string Truncate(string value) =>
        value.Length > UserDashboardPreference.MaxUserKeyLength ? value[..UserDashboardPreference.MaxUserKeyLength] : value;
}
