using DotMarc.Audit;
using DotMarc.Data;

namespace DotMarc.DomainImport;

public enum ExistingDomainMode { Skip, Add, Match }

public enum ImportRowStatus { New, AlreadyMonitored, Duplicate, Invalid }

public enum ImportNameKind { Group, Tag, HaloClient }

public enum NameChoice { Create, MapTo, LeaveOut }

public sealed record NameKey(ImportNameKind Kind, string LoweredName);

public sealed record NameResolution(NameChoice Choice, string? MapTo = null);

/// <summary>A group, tag or Halo client name in the input that doesn't exist, with the choice in effect for it.</summary>
public sealed record UnknownName(ImportNameKind Kind, string Name, IReadOnlyList<int> LineNumbers, IReadOnlyList<string> Suggestions, bool CanCreate, NameResolution Resolution)
{
    public NameKey Key => new(Kind, Name.ToLowerInvariant());
}

public sealed record ImportPermissions(bool CanEditDomains, bool CanManageMtaSts, bool CanAddGroups, bool CanAddTags)
{
    public static ImportPermissions All { get; } = new(true, true, true, true);
}

/// <summary>How a domain's groups or tags change: names to add and remove, or, with <see cref="ReplaceAll"/>, the
/// complete list it should end up with.</summary>
public sealed record NameSetChange(IReadOnlyList<string> Add, IReadOnlyList<string> Remove, bool ReplaceAll)
{
    public IReadOnlyList<string> ApplyTo(IEnumerable<string> current) =>
        ReplaceAll
            ? Add.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : current.Where(name => !Remove.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Concat(Add)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
}

public sealed record MtaStsTarget(bool Enabled, MtaStsMode Mode, IReadOnlyList<string> MxHosts, int MaxAgeSeconds);

/// <summary>What importing does to one domain. A null part leaves that part alone.</summary>
public sealed record DomainTarget(
    NameSetChange? Groups,
    NameSetChange? Tags,
    bool SetHaloClient,
    int? HaloClientId,
    bool? Monitored,
    IReadOnlyList<string>? DkimSelectors,
    MtaStsTarget? MtaSts);

/// <summary>One input row in the preview: its status, what it will change, what it will remove (shown in red), and
/// notes such as values left out.</summary>
public sealed record PlannedRow(
    int LineNumber,
    string RawDomain,
    string? Domain,
    ImportRowStatus Status,
    string? InvalidReason,
    int? ExistingDomainId,
    int? MergedIntoLine,
    DomainTarget? Target,
    IReadOnlyList<AuditFieldChange> Changes,
    IReadOnlyList<string> Removals,
    IReadOnlyList<string> Notes);

public sealed record ImportPlan(
    ExistingDomainMode Mode,
    IReadOnlyList<PlannedRow> Rows,
    IReadOnlyList<UnknownName> UnknownNames,
    IReadOnlyList<string> GroupsToCreate,
    IReadOnlyList<string> TagsToCreate,
    IReadOnlyList<string> Notices)
{
    public int NewCount => Rows.Count(row => row.Status == ImportRowStatus.New);
    public int UpdateCount => Rows.Count(row => row.Status == ImportRowStatus.AlreadyMonitored && row.Target is not null);
    public int UnchangedCount => Rows.Count(row => row.Status == ImportRowStatus.AlreadyMonitored && row.Target is null && Mode != ExistingDomainMode.Skip);
    public int SkippedExistingCount => Rows.Count(row => row.Status == ImportRowStatus.AlreadyMonitored && Mode == ExistingDomainMode.Skip);
    public int DuplicateCount => Rows.Count(row => row.Status == ImportRowStatus.Duplicate);
    public int InvalidCount => Rows.Count(row => row.Status == ImportRowStatus.Invalid);

    /// <summary>True while a name is set to "Map to" with nothing picked yet. The import waits for a choice rather than
    /// quietly leaving the name out.</summary>
    public bool HasUnfinishedChoices => UnknownNames.Any(name => name.Resolution is { Choice: NameChoice.MapTo, MapTo: null });
}
