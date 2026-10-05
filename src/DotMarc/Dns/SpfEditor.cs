using System.Security.Cryptography;
using System.Text;

namespace DotMarc.Dns;

/// <summary>The SPF editor's rules, kept out of the dialog so they can be tested and reused by the push.</summary>
public static class SpfEditor
{
    public const int LengthWarning = 450;

    /// <summary>Where the editor starts: the live record; several live records merged into one; or, with none, a
    /// record including the detected services with the default ending.</summary>
    public static (SpfRecord Record, bool IsMerge) StartingPoint(IReadOnlyList<string> liveValues, IReadOnlyList<string> suggestedIncludes, char defaultEnding)
    {
        if (liveValues.Count == 1)
        {
            return (SpfRecord.Parse(liveValues[0]), false);
        }

        if (liveValues.Count > 1)
        {
            var merged = SpfRecord.Merge(liveValues.Select(SpfRecord.Parse).ToList());
            return (merged.AllTerm is null && !merged.Terms.Any(term => term.IsRedirect) ? merged.WithAll(defaultEnding) : merged, true);
        }

        var record = SpfRecord.Parse("v=spf1").WithAll(defaultEnding);
        foreach (var include in suggestedIncludes)
        {
            record = record.WithInclude(include);
        }

        return (record, false);
    }

    /// <summary>Identifies the live SPF records the editor started from, so the push can tell if they changed.</summary>
    public static string Fingerprint(IEnumerable<string> liveValues) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', liveValues.Select(value => value.Trim()).Order(StringComparer.Ordinal)))));

    /// <summary>Why the proposed record can't be pushed, as a popup flag, or null if it can.</summary>
    public static string? BlockReason(string proposedText, SpfLookupCount proposedCount, IReadOnlyList<string> liveValues, SpfLookupCount? worstLiveCount)
    {
        if (liveValues.Count == 1 && SpfRecord.IsSpf(liveValues[0]) && SpfRecord.Parse(liveValues[0]).Format() == SpfRecord.Parse(proposedText).Format())
        {
            return "nothing-to-push";
        }

        var lowersAnOverLimitRecord = worstLiveCount is { IsOverLimit: true } && proposedCount.Total < worstLiveCount.Total;
        return proposedCount.IsOverLimit && !lowersAnOverLimitRecord ? "spf-too-many-lookups" : null;
    }
}
