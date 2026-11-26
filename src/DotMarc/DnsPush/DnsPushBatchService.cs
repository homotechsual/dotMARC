using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Reporting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DotMarc.DnsPush;

/// <summary>Makes a batch of DNS pushes and reviews it: works out, for each domain, what pushing the record would change, and stores that so
/// the person can look at it, untick domains, and then sign in once to push them all. Creating or changing a batch changes nothing in DNS.
/// Whether the person may push the target at all is checked by the endpoint that calls this, not here.</summary>
public sealed class DnsPushBatchService(
    DnsPushPlanner planner, IEnumerable<IDnsChangeBuilder> changeBuilders, IEnumerable<IDnsPushProvider> pushProviders, TimeProvider? timeProvider = null)
{
    /// <summary>The targets whose content comes from the domain and its live DNS. spf and dkim are typed per domain, and dmarc-auth writes into
    /// one other zone, so none of those is part of a batch.</summary>
    public static readonly IReadOnlyList<string> BulkTargets = ["dmarc", "tlsrpt", "mta-sts"];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public static async Task<DnsPushBatch> GetAsync(DotMarcDbContext context, AuditActor actor, Guid batchId, CancellationToken cancellationToken = default)
    {
        var owner = DashboardPreferencesStore.UserKeyOf(actor);
        return owner is not null
            ? await context.DnsPushBatches.AsNoTracking().Include(batch => batch.Domains)
                .SingleOrDefaultAsync(batch => batch.Id == batchId && batch.StartedBy == owner, cancellationToken).ConfigureAwait(false)
                ?? throw new DnsPushBatchException("That DNS batch is unavailable or belongs to another person.")
            : throw new DnsPushBatchException("That DNS batch is unavailable or belongs to another person.");
    }

    public async Task<DnsPushBatch> CreateAsync(
        DotMarcDbContext context, AuditActor actor, string provider, string target, IReadOnlyList<int> domainIds, CancellationToken cancellationToken = default)
    {
        // The person's key is the same one the Dashboard preferences use: the Entra object id, else the email. An API key has neither.
        var owner = DashboardPreferencesStore.UserKeyOf(actor)
            ?? throw new DnsPushBatchException("A batch of DNS pushes has to be started by a signed-in person, not by an API key.");

        var distinctIds = domainIds.Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            throw new DnsPushBatchException("Choose at least one domain.");
        }

        if (distinctIds.Count > DnsPushBatchLimits.MaxDomains)
        {
            throw new DnsPushBatchException($"A batch can have at most {DnsPushBatchLimits.MaxDomains} domains, and this one has {distinctIds.Count}. Split it into smaller batches.");
        }

        if (!BulkTargets.Contains(target, StringComparer.Ordinal) || changeBuilders.Find(target) is not { } builder)
        {
            throw new DnsPushBatchException($"Only {string.Join(", ", BulkTargets)} can be pushed to several domains at once.");
        }

        if (!pushProviders.Any(candidate => string.Equals(candidate.ProviderKey, provider, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DnsPushBatchException($"'{provider}' is not a DNS provider dotMARC can push to.");
        }

        var domains = (await context.Domains.AsNoTracking()
                .Include(domain => domain.DkimRecords)
                .Where(domain => distinctIds.Contains(domain.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(domain => domain.Id);

        var now = _timeProvider.GetUtcNow();
        var batch = new DnsPushBatch
        {
            Id = Guid.NewGuid(), StartedBy = owner, Provider = provider.ToLowerInvariant(), Target = target, Status = DnsPushBatchStatus.Planned,
            CreatedUtc = now, ExpiresUtc = now + DnsPushBatchLimits.ReviewWindow
        };

        for (var position = 0; position < distinctIds.Count; position++)
        {
            var domainId = distinctIds[position];
            batch.Domains.Add(domains.TryGetValue(domainId, out var domain)
                ? await ReviewAsync(planner, domain, builder, batch.Provider, position, cancellationToken).ConfigureAwait(false)
                : new DnsPushBatchDomain { DomainId = domainId, Position = position, Status = DnsPushBatchDomainStatus.Refused, Detail = "not-found" });
        }

        context.DnsPushBatches.Add(batch);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return batch;
    }

    private static async Task<DnsPushBatchDomain> ReviewAsync(
        DnsPushPlanner planner, Domain domain, IDnsChangeBuilder builder, string provider, int position, CancellationToken cancellationToken)
    {
        var row = new DnsPushBatchDomain { DomainId = domain.Id, DomainName = domain.Name, Position = position };
        var planning = await planner.PlanAsync(domain, builder, provider, payload: null, cancellationToken).ConfigureAwait(false);
        if (planning.Flag is not null)
        {
            row.Status = DnsPushBatchDomainStatus.Refused;
            row.Detail = planning.Flag;
        }
        else if (DnsPushReview.From(planning.Plan!).NothingToChange)
        {
            row.Status = DnsPushBatchDomainStatus.NothingToChange;
        }
        else
        {
            row.Status = DnsPushBatchDomainStatus.Planned;
            row.PlanFingerprint = DnsPlanFingerprint.Of(planning.Plan!.Changes);
            var review = DnsPushReview.From(planning.Plan);
            row.ReviewJson = JsonSerializer.Serialize(review);
            row.Included = review.LostDelegations.Count == 0;
        }

        return row;
    }

    /// <summary>Chooses which of the batch's Planned domains will be pushed: exactly the ones named. Only the person who started the batch can, and
    /// only while it is still Planned and inside its review window.</summary>
    public async Task SetIncludedAsync(
        DotMarcDbContext context, AuditActor actor, Guid batchId, IReadOnlyList<int> includedDomainIds, CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Serialize selection changes with the callback's claim, including edits from another tab.
        var owner = DashboardPreferencesStore.UserKeyOf(actor);
        await context.DnsPushBatches.Where(candidate => candidate.Id == batchId && candidate.StartedBy == owner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, candidate => candidate.Status), cancellationToken).ConfigureAwait(false);
        var batch = await context.DnsPushBatches.AsNoTracking().Include(candidate => candidate.Domains).SingleOrDefaultAsync(candidate => candidate.Id == batchId, cancellationToken).ConfigureAwait(false)
            ?? throw new DnsPushBatchException("That batch doesn't exist any more.");

        if (DashboardPreferencesStore.UserKeyOf(actor) != batch.StartedBy)
        {
            throw new DnsPushBatchException("That batch was started by someone else.");
        }

        if (batch.Status != DnsPushBatchStatus.Planned)
        {
            throw new DnsPushBatchException("That batch has already been run, so it can't be changed.");
        }

        if (batch.ExpiresUtc <= _timeProvider.GetUtcNow())
        {
            throw new DnsPushBatchException("This review has expired. Start a new batch.");
        }

        var chosen = includedDomainIds.ToHashSet();
        foreach (var domainId in chosen)
        {
            if (batch.Domains.FirstOrDefault(row => row.DomainId == domainId) is not { Status: DnsPushBatchDomainStatus.Planned })
            {
                throw new DnsPushBatchException("Only a domain with changes to push can be included.");
            }
        }

        await context.DnsPushBatchDomains.Where(row => row.BatchId == batchId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Included,
                row => row.Status == DnsPushBatchDomainStatus.Planned && chosen.Contains(row.DomainId)), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
