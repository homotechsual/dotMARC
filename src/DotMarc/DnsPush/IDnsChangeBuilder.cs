using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>The changes to push for one target, or the popup flag explaining why there's nothing to push.</summary>
public sealed record DnsChangePlan(IReadOnlyList<DnsRecordChange> Changes, string? Refusal)
{
    public static DnsChangePlan Of(params DnsRecordChange[] changes) => new(changes, null);

    public static DnsChangePlan Refuse(string flag) => new([], flag);
}

/// <summary>What a builder needs: the domain (with its DKIM records), the provider being pushed to, the domain's zone
/// (null for builders that don't write there), and the payload the push started with (the edited SPF record).</summary>
public sealed record DnsPushRequest(Domain Domain, string Provider, string? DomainZone, string? Payload);

/// <summary>Works out what one push target ("dmarc", "spf" and so on) changes. The /dns-push endpoints look the
/// builder up by target, so each target's permission and logic live in one place.</summary>
public interface IDnsChangeBuilder
{
    string Target { get; }

    /// <summary>The authorization policy a person needs to push this target.</summary>
    string RequiredPolicy { get; }

    /// <summary>False only for a target written to another domain's zone (dmarc-auth writes to the mailbox's
    /// domain), which finds that zone itself.</summary>
    bool WritesToDomainZone { get; }

    Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken);
}

public static class DnsChangeBuilderLookup
{
    public static IDnsChangeBuilder? Find(this IEnumerable<IDnsChangeBuilder> builders, string? target) =>
        target is null ? null : builders.FirstOrDefault(builder => builder.Target == target);
}
