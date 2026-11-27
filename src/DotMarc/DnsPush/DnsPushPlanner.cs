using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>Either a plan to push, or a flag saying why there is nothing to push. The flags are the ones the push popup reports:
/// "zone-not-found", "error", and whatever refusal a change builder gives.</summary>
public sealed record DnsPushPlanResult(DnsChangePlan? Plan, string? Flag);

/// <summary>Works out what to push for one domain at the moment it is pushed: finds the domain's zone again, refuses if its DNS is no
/// longer with the provider being pushed to, and builds the plan. Shared by the single-domain push and by batches, so both decide the same
/// way. Resolving the zone here, rather than trusting what a page showed when it loaded, catches a domain whose DNS moved provider in
/// between, which is treated as zone not found and never pushed to the wrong place.</summary>
public sealed class DnsPushPlanner(IDnsProviderDetector detector)
{
    public async Task<DnsPushPlanResult> PlanAsync(Domain domain, IDnsChangeBuilder builder, string provider, string? payload, CancellationToken cancellationToken)
    {
        // Every target except dmarc-auth writes into the domain's own zone. dmarc-auth writes into the mailbox's domain's zone instead and
        // finds that itself, so it needs no detection here.
        string? domainZone = null;
        if (builder.WritesToDomainZone)
        {
            DnsProviderDetectionResult detection;
            try
            {
                detection = await detector.DetectAsync(domain.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // A transient DNS lookup failure is not the same as "zone not found" (the lookup worked, but no configured account owns that
                // zone), so it is a generic error and doesn't imply a permissions or configuration problem that doesn't exist.
                return new DnsPushPlanResult(null, "error");
            }

            if (!string.Equals(detection.Provider.ToProviderKey(), provider, StringComparison.OrdinalIgnoreCase))
            {
                return new DnsPushPlanResult(null, "zone-not-found");
            }

            domainZone = detection.ZoneName;
        }

        var plan = await builder.BuildAsync(new DnsPushRequest(domain, provider, domainZone, payload), cancellationToken).ConfigureAwait(false);
        return plan.Refusal is not null ? new DnsPushPlanResult(null, plan.Refusal) : new DnsPushPlanResult(plan, null);
    }
}
