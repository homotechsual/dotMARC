using System.Globalization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>What is wrong with a DMARC record, judged against RFC 9989. The first seven kinds are breaking (receivers may
/// ignore the record or read it as p=none); the last two are informational.</summary>
public enum DmarcRecordProblemKind
{
    VersionMissingOrNotFirst,
    MultipleRecords,
    RepeatedTag,
    MissingPolicy,
    InvalidPolicy,
    InvalidSubdomainPolicy,
    InvalidNonExistentPolicy,
    ObsoleteTag,
    TestingMode
}

public static class DmarcRecordProblemKindExtensions
{
    /// <summary>True for a problem that can make receivers ignore the record or read it as p=none.</summary>
    public static bool IsBreaking(this DmarcRecordProblemKind kind) => kind <= DmarcRecordProblemKind.InvalidNonExistentPolicy;
}

/// <summary>One problem found in a DMARC record. <paramref name="Tag"/> is set for <see cref="DmarcRecordProblemKind.RepeatedTag"/>
/// and <see cref="DmarcRecordProblemKind.ObsoleteTag"/>.</summary>
public sealed record DmarcRecordProblem(DmarcRecordProblemKind Kind, string? Tag, string Description);

/// <summary>One tag of a DMARC record: its name and value as written (trimmed).</summary>
internal sealed record DmarcTag(string Name, string Value)
{
    public string Raw => $"{Name}={Value}";

    public bool IsNamed(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
}

internal static class DmarcTagList
{
    /// <summary>Splits a TXT value into its tags. Empty parts and parts without a name and an equals sign are skipped.</summary>
    public static List<DmarcTag> Parse(string value)
    {
        var tags = new List<DmarcTag>();
        foreach (var part in value.Split(';'))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part[..separator].Trim() is { Length: > 0 } name)
            {
                tags.Add(new DmarcTag(name, part[(separator + 1)..].Trim()));
            }
        }

        return tags;
    }

    public static DmarcPolicyLevel? ParseLevel(string text) => text.ToLowerInvariant() switch
    {
        "none" => DmarcPolicyLevel.None,
        "quarantine" => DmarcPolicyLevel.Quarantine,
        "reject" => DmarcPolicyLevel.Reject,
        _ => null
    };

    public static string Name(DmarcPolicyLevel level) => level.ToString().ToLowerInvariant();

    /// <summary>True for a value whose first tag is v=DMARC1 (name ignoring case, value exact).</summary>
    public static bool IsProperRecord(List<DmarcTag> tags) =>
        tags.Count > 0 && tags[0].IsNamed("v") && tags[0].Value == "DMARC1";

    /// <summary>True for a value that looks like a DMARC record gone wrong: its first tag is v with another value, or it
    /// holds a v=DMARC1 tag that is not first.</summary>
    public static bool IsCandidate(List<DmarcTag> tags) =>
        tags.Count > 0 && (tags[0].IsNamed("v") || tags.Any(tag => tag.IsNamed("v") && tag.Value == "DMARC1"));

    /// <summary>The strictest valid level among the tags named <paramref name="name"/>, or null when none is valid.</summary>
    public static DmarcPolicyLevel? Strictest(IEnumerable<DmarcTag> tags, string name)
    {
        DmarcPolicyLevel? strictest = null;
        foreach (var tag in tags.Where(tag => tag.IsNamed(name)))
        {
            if (ParseLevel(tag.Value) is { } level && (strictest is null || level > strictest))
            {
                strictest = level;
            }
        }

        return strictest;
    }
}

/// <summary>A DMARC record (or several) read the way RFC 9989 reads it, with each problem named. See
/// <see cref="DmarcRecordRepair"/> for the clean record that fixes them.</summary>
public sealed record DmarcRecordAnalysis(
    IReadOnlyList<string> Records,
    IReadOnlyList<DmarcRecordProblem> Problems,
    DmarcPolicyLevel? Policy,
    DmarcPolicyLevel? SubdomainPolicy,
    DmarcPolicyLevel? NonExistentPolicy,
    int? Percent,
    bool Testing,
    bool HasValidRua)
{
    private static readonly string[] ObsoleteTagNames = ["pct", "rf", "ri"];

    public bool IsValid => !Problems.Any(problem => problem.Kind.IsBreaking());

    public DmarcPolicyLevel? EffectiveSubdomainPolicy => SubdomainPolicy ?? Policy;

    public DmarcPolicyLevel? EffectiveNonExistentPolicy => NonExistentPolicy ?? SubdomainPolicy ?? Policy;

    /// <summary>Analyses the TXT values at _dmarc.&lt;domain&gt;. Values that are not DMARC (no v tag) are ignored. When
    /// there is no proper v=DMARC1 record, a value that looks like one is analysed instead and reported as a version problem.</summary>
    public static DmarcRecordAnalysis Analyse(IEnumerable<string> txtValues)
    {
        var proper = new List<string>();
        var candidates = new List<string>();
        foreach (var value in txtValues)
        {
            var tags = DmarcTagList.Parse(value);
            if (DmarcTagList.IsProperRecord(tags))
            {
                proper.Add(value);
            }
            else if (DmarcTagList.IsCandidate(tags))
            {
                candidates.Add(value);
            }
        }

        var records = proper.Count > 0 ? proper : candidates;
        var problems = new List<DmarcRecordProblem>();
        if (records.Count == 0)
        {
            return new DmarcRecordAnalysis(records, problems, null, null, null, null, false, false);
        }

        if (proper.Count == 0)
        {
            problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.VersionMissingOrNotFirst, null,
                "The record does not start with v=DMARC1. Receivers ignore it."));
        }

        if (proper.Count > 1)
        {
            problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.MultipleRecords, null,
                $"There are {proper.Count} DMARC records. Receivers discard all of them."));
        }

        // Every tag but v, in record order; v is allowed once per record.
        var tagsInOrder = records.SelectMany(DmarcTagList.Parse).Where(tag => !tag.IsNamed("v")).ToList();

        foreach (var name in tagsInOrder.Select(tag => tag.Name.ToLowerInvariant()).Distinct())
        {
            if (tagsInOrder.Count(tag => tag.IsNamed(name)) > 1)
            {
                problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.RepeatedTag, name,
                    $"The {name} tag appears more than once. Receivers may ignore the whole record."));
            }
        }

        var policy = DmarcTagList.Strictest(tagsInOrder, "p");
        var subdomainPolicy = DmarcTagList.Strictest(tagsInOrder, "sp");
        var nonExistentPolicy = DmarcTagList.Strictest(tagsInOrder, "np");

        if (!tagsInOrder.Any(tag => tag.IsNamed("p")))
        {
            problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.MissingPolicy, null,
                "The record has no p tag. Receivers read it as p=none, or apply no DMARC processing without a valid rua."));
        }
        else if (tagsInOrder.Any(tag => tag.IsNamed("p") && DmarcTagList.ParseLevel(tag.Value) is null))
        {
            problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.InvalidPolicy, null,
                "The p tag has a value that is not none, quarantine or reject. Receivers read the record as p=none, or apply no DMARC processing without a valid rua."));
        }

        if (tagsInOrder.Any(tag => tag.IsNamed("sp") && DmarcTagList.ParseLevel(tag.Value) is null))
        {
            problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.InvalidSubdomainPolicy, null,
                "The sp tag has a value that is not none, quarantine or reject. Receivers read the record as p=none, or apply no DMARC processing without a valid rua."));
        }

        if (tagsInOrder.Any(tag => tag.IsNamed("np") && DmarcTagList.ParseLevel(tag.Value) is null))
        {
            problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.InvalidNonExistentPolicy, null,
                "The np tag has a value that is not none, quarantine or reject. Receivers read the record as p=none, or apply no DMARC processing without a valid rua."));
        }

        foreach (var name in ObsoleteTagNames.Where(name => tagsInOrder.Any(tag => tag.IsNamed(name))))
        {
            problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.ObsoleteTag, name,
                $"{name} is obsolete in RFC 9989 and newer receivers ignore it."));
        }

        // A repeated t keeps the value that enforces more, so t=n beats t=y.
        var testValues = tagsInOrder.Where(tag => tag.IsNamed("t")).Select(tag => tag.Value.ToLowerInvariant()).ToList();
        var testing = testValues.Contains("y") && !testValues.Contains("n");
        if (testing)
        {
            problems.Add(new DmarcRecordProblem(DmarcRecordProblemKind.TestingMode, "t",
                "t=y asks receivers not to apply the policy (testing mode)."));
        }

        int? percent = tagsInOrder
            .Where(tag => tag.IsNamed("pct"))
            .Select(tag => int.TryParse(tag.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 100 ? parsed : (int?)null)
            .FirstOrDefault(parsed => parsed is not null);

        var hasValidRua = records.Any(record => DmarcRua.Parse(record).Any(entry => entry.HasMailtoScheme));

        return new DmarcRecordAnalysis(records, problems, policy, subdomainPolicy, nonExistentPolicy, percent, testing, hasValidRua);
    }
}
