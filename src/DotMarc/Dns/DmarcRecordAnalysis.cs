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
public sealed record DmarcRecordProblem(DmarcRecordProblemKind Kind, string? Tag, string Description)
{
    private const string InvalidValueEffect = "Receivers read the record as p=none, or apply no DMARC processing without a valid rua.";

    /// <summary>A problem of this kind, with its plain-words description.</summary>
    public static DmarcRecordProblem Of(DmarcRecordProblemKind kind, string? tag = null) => new(kind, tag, Describe(kind, tag));

    /// <summary>The plain-words description of a problem, shared by the analyser and by anything that reads a stored problem.</summary>
    public static string Describe(DmarcRecordProblemKind kind, string? tag) => kind switch
    {
        DmarcRecordProblemKind.VersionMissingOrNotFirst => "The record does not start with v=DMARC1. Receivers ignore it.",
        DmarcRecordProblemKind.MultipleRecords => "There is more than one DMARC record. Receivers discard all of them.",
        DmarcRecordProblemKind.RepeatedTag => $"The {tag} tag appears more than once. Receivers may ignore the whole record.",
        DmarcRecordProblemKind.MissingPolicy => "The record has no p tag. Receivers read it as p=none, or apply no DMARC processing without a valid rua.",
        DmarcRecordProblemKind.InvalidPolicy => $"The p tag has a value that is not none, quarantine or reject. {InvalidValueEffect}",
        DmarcRecordProblemKind.InvalidSubdomainPolicy => $"The sp tag has a value that is not none, quarantine or reject. {InvalidValueEffect}",
        DmarcRecordProblemKind.InvalidNonExistentPolicy => $"The np tag has a value that is not none, quarantine or reject. {InvalidValueEffect}",
        DmarcRecordProblemKind.ObsoleteTag => $"{tag} is obsolete in RFC 9989 and newer receivers ignore it.",
        _ => "t=y asks receivers not to apply the policy (testing mode)."
    };

    /// <summary>The text stored for a problem: its kind, then a colon and the tag when the kind names one.</summary>
    public string Stored => Tag is null || Kind == DmarcRecordProblemKind.TestingMode ? Kind.ToString() : $"{Kind}:{Tag}";

    /// <summary>Reads a stored problem back, or null for text this version does not know, so older rows never break a page.</summary>
    public static DmarcRecordProblem? Parse(string stored)
    {
        var separator = stored.IndexOf(':');
        var kindText = separator < 0 ? stored : stored[..separator];
        var tag = separator < 0 ? null : stored[(separator + 1)..];
        if (!Enum.TryParse<DmarcRecordProblemKind>(kindText, out var kind) || !Enum.IsDefined(kind))
        {
            return null;
        }

        var needsTag = kind is DmarcRecordProblemKind.RepeatedTag or DmarcRecordProblemKind.ObsoleteTag;
        return needsTag && string.IsNullOrEmpty(tag) ? null : Of(kind, needsTag ? tag : null);
    }
}

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

    /// <summary>True for a value that looks like a DMARC record gone wrong: its first tag is v with a value starting with
    /// DMARC (any case), or it holds a v=DMARC1 tag that is not first. Other v values, such as v=spf1, are not DMARC.</summary>
    public static bool IsCandidate(List<DmarcTag> tags) =>
        tags.Count > 0
        && ((tags[0].IsNamed("v") && tags[0].Value.StartsWith("DMARC", StringComparison.OrdinalIgnoreCase))
            || tags.Any(tag => tag.IsNamed("v") && tag.Value == "DMARC1"));

    /// <summary>The parts of a TXT value that <see cref="Parse"/> cannot read as a tag, trimmed.</summary>
    public static List<string> UnreadableParts(string value) =>
        value.Split(';')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0 && !(part.IndexOf('=') is var separator and > 0 && part[..separator].Trim().Length > 0))
            .ToList();

    /// <summary>The text after a leading DMARC1 in a version tag's value (for example the p=reject of "v=DMARC1 p=reject"),
    /// or null when there is none.</summary>
    public static string? TextAfterVersion(string versionValue) =>
        versionValue.StartsWith("DMARC1", StringComparison.OrdinalIgnoreCase) && versionValue[6..].Trim() is { Length: > 0 } rest ? rest : null;

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
    IReadOnlyList<string> IgnoredCandidates)
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
            return new DmarcRecordAnalysis(records, problems, null, null, null, null, false, []);
        }

        if (proper.Count == 0)
        {
            problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.VersionMissingOrNotFirst));
        }

        if (proper.Count > 1)
        {
            problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.MultipleRecords));
        }

        // Every tag but v, in record order; v is allowed once per record.
        var tagsInOrder = records.SelectMany(DmarcTagList.Parse).Where(tag => !tag.IsNamed("v")).ToList();

        foreach (var name in tagsInOrder.Select(tag => tag.Name.ToLowerInvariant()).Distinct())
        {
            if (tagsInOrder.Count(tag => tag.IsNamed(name)) > 1)
            {
                problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.RepeatedTag, name));
            }
        }

        var policy = DmarcTagList.Strictest(tagsInOrder, "p");
        var subdomainPolicy = DmarcTagList.Strictest(tagsInOrder, "sp");
        var nonExistentPolicy = DmarcTagList.Strictest(tagsInOrder, "np");

        if (!tagsInOrder.Any(tag => tag.IsNamed("p")))
        {
            problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.MissingPolicy));
        }
        else if (tagsInOrder.Any(tag => tag.IsNamed("p") && DmarcTagList.ParseLevel(tag.Value) is null))
        {
            problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.InvalidPolicy));
        }

        if (tagsInOrder.Any(tag => tag.IsNamed("sp") && DmarcTagList.ParseLevel(tag.Value) is null))
        {
            problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.InvalidSubdomainPolicy));
        }

        if (tagsInOrder.Any(tag => tag.IsNamed("np") && DmarcTagList.ParseLevel(tag.Value) is null))
        {
            problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.InvalidNonExistentPolicy));
        }

        foreach (var name in ObsoleteTagNames.Where(name => tagsInOrder.Any(tag => tag.IsNamed(name))))
        {
            problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.ObsoleteTag, name));
        }

        // A repeated t keeps the value that enforces more, so t=n beats t=y.
        var testValues = tagsInOrder.Where(tag => tag.IsNamed("t")).Select(tag => tag.Value.ToLowerInvariant()).ToList();
        var testing = testValues.Contains("y") && !testValues.Contains("n");
        if (testing)
        {
            problems.Add(DmarcRecordProblem.Of(DmarcRecordProblemKind.TestingMode, "t"));
        }

        int? percent = tagsInOrder
            .Where(tag => tag.IsNamed("pct"))
            .Select(tag => int.TryParse(tag.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 100 ? parsed : (int?)null)
            .FirstOrDefault(parsed => parsed is not null);

        return new DmarcRecordAnalysis(records, problems, policy, subdomainPolicy, nonExistentPolicy, percent, testing,
            proper.Count > 0 ? candidates : []);
    }
}
