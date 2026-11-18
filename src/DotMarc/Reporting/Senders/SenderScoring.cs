using System.Net;
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Reporting;

public enum SenderSignal { AuthDomainPassed, AuthDomainFailed, DkimSelector, IpOwner, SpfInclude, SpfRange }

/// <summary>One reason we think a source belongs to a service, in words a person can read.</summary>
public sealed record SenderEvidence(SenderSignal Signal, string Description);

public sealed record ServiceScore(SenderService Service, double Score, IReadOnlyList<SenderEvidence> Evidence);

/// <summary>What scoring needs to know about one source IP. <see cref="Owner"/> is the RDAP organisation, null until looked up.</summary>
public sealed record SenderSourceInput(string SourceIp, string? Owner, IReadOnlyList<AuthDetailSummary> AuthDetails);

/// <summary>Scores how strongly a source's evidence points at each sending service. The weights are guesses to be tuned against
/// real reports, so they live together here. The result is a score of the evidence found, not a probability.</summary>
public static class SenderScoring
{
    public const double AuthDomainPassedWeight = 0.90;
    public const double AuthDomainFailedWeight = 0.80;
    public const double DkimSelectorWeight = 0.60;
    public const double IpOwnerWeight = 0.50;
    public const double SpfIncludeWeight = 0.20;
    public const double SpfRangeWeight = 0.85;

    public const double Cap = 0.99;
    public const double AttributionThreshold = 0.30;
    public const double StrongFrom = 0.70;
    public const double ModerateFrom = 0.40;

    /// <summary>Every service with some evidence, highest score first, ties in catalogue order.
    /// <paramref name="spfIncludedNames"/> are the sending services the domain's own SPF record includes. <paramref name="spfRanges"/>
    /// are the IP ranges that record authorises: a source inside one tagged with a service has stronger evidence than the name alone.</summary>
    public static IReadOnlyList<ServiceScore> Score(
        SenderSourceInput source, IReadOnlyCollection<string> spfIncludedNames, IReadOnlyList<SpfServiceRange>? spfRanges = null)
    {
        var scores = new List<ServiceScore>();
        var rangeServices = ServicesAuthorisingIp(source.SourceIp, spfRanges);
        foreach (var service in SenderServiceCatalog.All)
        {
            var evidence = new List<(SenderEvidence Evidence, double Weight)>();

            var matchingDetails = source.AuthDetails.Where(detail => SenderServiceCatalog.MatchesAuthDomain(service, detail.Domain)).ToList();
            var passed = matchingDetails.FirstOrDefault(detail => detail.Result == DmarcMechanismResult.Pass);
            if (passed is not null)
            {
                evidence.Add((new SenderEvidence(SenderSignal.AuthDomainPassed, AuthDomainSentence(passed, "passed")), AuthDomainPassedWeight));
            }
            else if (matchingDetails.Count > 0)
            {
                evidence.Add((new SenderEvidence(SenderSignal.AuthDomainFailed, AuthDomainSentence(matchingDetails[0], "failed")), AuthDomainFailedWeight));
            }

            var selectorDetail = source.AuthDetails.FirstOrDefault(detail =>
                detail.Mechanism == DmarcAuthMechanism.Dkim && SenderServiceCatalog.MatchesSelector(service, detail.Selector));
            if (selectorDetail is not null)
            {
                evidence.Add((new SenderEvidence(SenderSignal.DkimSelector, $"DKIM selector {selectorDetail.Selector} is used by {service.Name}"), DkimSelectorWeight));
            }

            if (SenderServiceCatalog.MatchesOwner(service, source.Owner))
            {
                evidence.Add((new SenderEvidence(SenderSignal.IpOwner, $"IP owned by {source.Owner}"), IpOwnerWeight));
            }

            if (rangeServices.Contains(service.Name))
            {
                evidence.Add((new SenderEvidence(SenderSignal.SpfRange, $"Your SPF record authorises this IP for {service.Name}"), SpfRangeWeight));
            }
            else if (spfIncludedNames.Any(name => service.Name == name || service.DetectedNames.Contains(name)))
            {
                evidence.Add((new SenderEvidence(SenderSignal.SpfInclude, $"The domain's SPF record includes {service.Name}"), SpfIncludeWeight));
            }

            if (evidence.Count == 0)
            {
                continue;
            }

            var combined = 1 - evidence.Aggregate(1.0, (product, item) => product * (1 - item.Weight));
            scores.Add(new ServiceScore(service, Math.Min(combined, Cap), evidence.Select(item => item.Evidence).ToList()));
        }

        return scores.OrderByDescending(score => score.Score).ToList();
    }

    /// <summary>The names of the services whose SPF-authorised ranges contain the IP. A source IP that isn't an address matches none.</summary>
    private static HashSet<string> ServicesAuthorisingIp(string sourceIp, IReadOnlyList<SpfServiceRange>? spfRanges)
    {
        var services = new HashSet<string>(StringComparer.Ordinal);
        if (spfRanges is not { Count: > 0 } || !IPAddress.TryParse(sourceIp, out var address))
        {
            return services;
        }

        foreach (var range in spfRanges)
        {
            if (range.Service is not null && CidrRange.TryParse(range.Cidr, out var parsed) && parsed.Contains(address))
            {
                services.Add(range.Service);
            }
        }

        return services;
    }

    private static string AuthDomainSentence(AuthDetailSummary detail, string outcome) =>
        detail.Mechanism == DmarcAuthMechanism.Dkim
            ? $"DKIM signed by {detail.Domain} and {outcome}"
            : $"SPF checked for {detail.Domain} and {outcome}";
}
