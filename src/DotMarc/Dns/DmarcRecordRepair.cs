using DotMarc.Baselines;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>A clean DMARC record and a plain-words line for each change made to get there.</summary>
public sealed record DmarcRecordRepairResult(string Record, IReadOnlyList<string> Changes);

/// <summary>Builds the clean record for a DMARC record with problems, raised to a target when there is one. Never lowers
/// p, sp, np or pct enforcement.</summary>
public static class DmarcRecordRepair
{
    /// <summary>Returns the clean record, or null when there is nothing to change (no DMARC record, or the record
    /// is already clean and at or above the target).</summary>
    public static DmarcRecordRepairResult? Build(DmarcRecordAnalysis analysis, EffectiveDmarcTarget? target)
    {
        if (analysis.Records.Count == 0)
        {
            return null;
        }

        var versionChanges = new List<string>();
        var repeatChanges = new List<string>();
        var invalidChanges = new List<string>();
        var obsoleteChanges = new List<string>();
        var raiseChanges = new List<string>();
        var testingChanges = new List<string>();

        if (analysis.Records.Count > 1)
        {
            versionChanges.Add($"Combined {analysis.Records.Count} DMARC records into one.");
        }

        var parsedRecords = analysis.Records.Select(DmarcTagList.Parse).ToList();
        if (analysis.Problems.Any(problem => problem.Kind == DmarcRecordProblemKind.VersionMissingOrNotFirst))
        {
            var hasVersionTag = parsedRecords.SelectMany(tags => tags).Any(tag => tag.IsNamed("v") && tag.Value == "DMARC1");
            versionChanges.Add(hasVersionTag ? "Moved v=DMARC1 to the front." : "Set the version to v=DMARC1.");
        }

        var allTags = parsedRecords.SelectMany(tags => tags).Where(tag => !tag.IsNamed("v")).ToList();
        var output = new List<DmarcTag>();
        foreach (var name in allTags.Select(tag => tag.Name.ToLowerInvariant()).Distinct())
        {
            var occurrences = allTags.Where(tag => tag.IsNamed(name)).ToList();
            var first = occurrences[0];
            DmarcTag? kept = first;

            if (name is "p" or "sp" or "np")
            {
                if (DmarcTagList.Strictest(occurrences, name) is { } strictest)
                {
                    // The first occurrence carrying the strictest valid value stays, at the first occurrence's position.
                    var keeper = occurrences.First(tag => DmarcTagList.ParseLevel(tag.Value) == strictest);
                    foreach (var dropped in occurrences.Where(tag => !ReferenceEquals(tag, keeper)))
                    {
                        repeatChanges.Add($"Removed the repeated {dropped.Raw}.");
                    }

                    kept = new DmarcTag(first.Name, DmarcTagList.Name(strictest));
                }
                else if (name == "p")
                {
                    invalidChanges.Add($"Replaced the invalid {first.Raw} with {first.Name}=none.");
                    foreach (var dropped in occurrences.Skip(1))
                    {
                        repeatChanges.Add($"Removed the repeated {dropped.Raw}.");
                    }

                    kept = new DmarcTag(first.Name, "none");
                }
                else
                {
                    foreach (var dropped in occurrences)
                    {
                        invalidChanges.Add($"Removed the invalid {dropped.Raw}.");
                    }

                    kept = null;
                }
            }
            else
            {
                // A repeated t keeps t=n if there is one, as the value that enforces more; other tags keep the first.
                var keeper = name == "t"
                    ? occurrences.FirstOrDefault(tag => string.Equals(tag.Value, "n", StringComparison.OrdinalIgnoreCase)) ?? first
                    : first;
                foreach (var dropped in occurrences.Where(tag => !ReferenceEquals(tag, keeper)))
                {
                    repeatChanges.Add($"Removed the repeated {dropped.Raw}.");
                }

                kept = keeper;
                if (name is "rf" or "ri" || (name == "pct" && target is not null))
                {
                    obsoleteChanges.Add($"Removed the obsolete {first.Raw}.");
                    kept = null;
                }
                else if (name == "t" && target is not null && string.Equals(keeper.Value, "y", StringComparison.OrdinalIgnoreCase))
                {
                    testingChanges.Add($"Removed {keeper.Raw} (testing mode).");
                    kept = null;
                }
            }

            if (kept is not null)
            {
                output.Add(kept);
            }
        }

        if (!output.Any(tag => tag.IsNamed("p")))
        {
            output.Insert(0, new DmarcTag("p", "none"));
            invalidChanges.Add("Added p=none because the record had no p.");
        }

        if (target is not null)
        {
            Raise(output, target, raiseChanges);
        }

        var changes = versionChanges.Concat(repeatChanges).Concat(invalidChanges).Concat(obsoleteChanges)
            .Concat(raiseChanges).Concat(testingChanges).ToList();
        if (changes.Count == 0)
        {
            return null;
        }

        var record = string.Join("; ", output.Select(tag => tag.Raw).Prepend("v=DMARC1"));
        return new DmarcRecordRepairResult(record, changes);
    }

    private static void Raise(List<DmarcTag> output, EffectiveDmarcTarget target, List<string> raiseChanges)
    {
        var policyIndex = output.FindIndex(tag => tag.IsNamed("p"));
        var policy = DmarcTagList.ParseLevel(output[policyIndex].Value)!.Value;
        var originalPolicy = policy;
        var policyRaised = false;
        if (policy < target.Policy)
        {
            raiseChanges.Add($"Raised {output[policyIndex].Name} from {DmarcTagList.Name(policy)} to {DmarcTagList.Name(target.Policy)}.");
            policy = target.Policy;
            policyRaised = true;
            output[policyIndex] = new DmarcTag(output[policyIndex].Name, DmarcTagList.Name(policy));
        }

        var subdomainIndex = output.FindIndex(tag => tag.IsNamed("sp"));
        if (subdomainIndex >= 0)
        {
            var subdomainPolicy = DmarcTagList.ParseLevel(output[subdomainIndex].Value)!.Value;
            if (subdomainPolicy < target.SubdomainPolicy)
            {
                raiseChanges.Add($"Raised {output[subdomainIndex].Name} from {DmarcTagList.Name(subdomainPolicy)} to {DmarcTagList.Name(target.SubdomainPolicy)}.");
                output[subdomainIndex] = new DmarcTag(output[subdomainIndex].Name, DmarcTagList.Name(target.SubdomainPolicy));
            }
        }
        else
        {
            // With no sp, subdomains followed p. Write the stricter of that and the target's sp when it differs from the
            // new p, so raising p does not push subdomains past what they had or what the target asks.
            var subdomainPolicy = originalPolicy > target.SubdomainPolicy ? originalPolicy : target.SubdomainPolicy;
            if (subdomainPolicy != policy)
            {
                var level = DmarcTagList.Name(subdomainPolicy);
                raiseChanges.Add(policyRaised ? $"Set sp to {level} so subdomains keep their policy." : $"Added sp={level}.");
                output.Insert(policyIndex + 1, new DmarcTag("sp", level));
            }
        }

        // An absent np follows sp, which now meets the target; only a written np can be weaker.
        var nonExistentIndex = output.FindIndex(tag => tag.IsNamed("np"));
        if (nonExistentIndex >= 0)
        {
            var nonExistentPolicy = DmarcTagList.ParseLevel(output[nonExistentIndex].Value)!.Value;
            if (nonExistentPolicy < target.SubdomainPolicy)
            {
                raiseChanges.Add($"Raised {output[nonExistentIndex].Name} from {DmarcTagList.Name(nonExistentPolicy)} to {DmarcTagList.Name(target.SubdomainPolicy)}.");
                output[nonExistentIndex] = new DmarcTag(output[nonExistentIndex].Name, DmarcTagList.Name(target.SubdomainPolicy));
            }
        }
    }
}
