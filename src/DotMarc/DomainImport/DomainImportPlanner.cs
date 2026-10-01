using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;

namespace DotMarc.DomainImport;

/// <summary>Works out what an import will do, row by row, without touching the database: a pure function of the input,
/// the snapshot, the existing-domains mode, the person's permissions and their choices for unknown names.</summary>
public static class DomainImportPlanner
{
    private const int DefaultMaxAgeSeconds = 604_800;

    public static ImportPlan Plan(ImportTable table, ImportSnapshot snapshot, ExistingDomainMode mode, ImportPermissions permissions,
        IReadOnlyDictionary<NameKey, NameResolution>? resolutions = null)
    {
        var notices = new List<string>();
        var usable = UsableColumns.For(table.Columns, snapshot, permissions, notices);

        // Normalise every row, and merge rows naming the same domain into the first one.
        var entries = new List<(ImportTableRow Row, string? Domain, string? InvalidReason, MergedRow? Primary)>();
        var primaries = new Dictionary<string, MergedRow>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            if (!DomainNameValidator.TryNormalize(row.RawDomain, out var domain, out var reason))
            {
                entries.Add((row, null, reason, null));
            }
            else if (primaries.TryGetValue(domain, out var primary))
            {
                primary.Merge(row);
                entries.Add((row, domain, null, primary));
            }
            else
            {
                var merged = new MergedRow(row, domain);
                primaries[domain] = merged;
                entries.Add((row, domain, null, null));
            }
        }

        var names = new NameResolver(snapshot, permissions, resolutions ?? new Dictionary<NameKey, NameResolution>());
        foreach (var primary in primaries.Values)
        {
            if (usable.Groups)
            {
                foreach (var name in primary.Groups?.Names ?? [])
                {
                    names.Consider(ImportNameKind.Group, name, primary.LineNumber);
                }
            }

            if (usable.Tags)
            {
                foreach (var name in primary.Tags?.Names ?? [])
                {
                    names.Consider(ImportNameKind.Tag, name, primary.LineNumber);
                }
            }

            if (usable.HaloClient && primary.HaloClient is { } haloClientName)
            {
                names.Consider(ImportNameKind.HaloClient, haloClientName, primary.LineNumber);
            }
        }

        var plannedRows = entries.Select(entry =>
        {
            if (entry.Domain is null)
            {
                return new PlannedRow(entry.Row.LineNumber, entry.Row.RawDomain, null, ImportRowStatus.Invalid, entry.InvalidReason,
                    null, null, null, [], [], entry.Row.Problems);
            }

            if (entry.Primary is { } mergedInto)
            {
                return new PlannedRow(entry.Row.LineNumber, entry.Row.RawDomain, entry.Domain, ImportRowStatus.Duplicate, null,
                    null, mergedInto.LineNumber, null, [], [], [$"The same domain as line {mergedInto.LineNumber}, so it was merged into that row."]);
            }

            var primaryRow = primaries[entry.Domain];
            snapshot.DomainsByName.TryGetValue(entry.Domain, out var existing);
            return new RowPlanner(primaryRow, existing, snapshot, mode, usable, table.Columns, names).Plan();
        }).ToList();

        return new ImportPlan(mode, plannedRows, names.UnknownNames(), names.ToCreate(ImportNameKind.Group), names.ToCreate(ImportNameKind.Tag), notices);
    }

    /// <summary>Which columns this import can use, given the input, the person's permissions and whether Halo is
    /// connected. Adds a notice for each column it has to ignore.</summary>
    private sealed record UsableColumns(bool Groups, bool Tags, bool HaloClient, bool Monitored, bool DkimSelectors, bool MtaSts)
    {
        public static UsableColumns For(IReadOnlySet<ImportColumn> columns, ImportSnapshot snapshot, ImportPermissions permissions, List<string> notices)
        {
            var editColumns = new (ImportColumn Column, string Label)[]
            {
                (ImportColumn.Groups, "Groups"), (ImportColumn.Tags, "Tags"), (ImportColumn.HaloClient, "Halo client"),
                (ImportColumn.Monitored, "Monitored"), (ImportColumn.DkimSelectors, "DKIM selectors"),
            }.Where(column => columns.Contains(column.Column)).Select(column => column.Label).ToList();

            if (!permissions.CanEditDomains && editColumns.Count > 0)
            {
                notices.Add($"The {JoinLabels(editColumns)} {(editColumns.Count == 1 ? "column was" : "columns were")} ignored: you don't have permission to change a domain's groups, tags and settings.");
            }

            var hasMtaStsColumns = columns.Overlaps([ImportColumn.MtaStsMode, ImportColumn.MtaStsMxHosts, ImportColumn.MtaStsMaxAge]);
            if (!permissions.CanManageMtaSts && hasMtaStsColumns)
            {
                notices.Add("The MTA-STS columns were ignored: you don't have permission to manage MTA-STS.");
            }

            var haloColumnUsable = permissions.CanEditDomains && columns.Contains(ImportColumn.HaloClient);
            if (haloColumnUsable && snapshot.HaloClients is null)
            {
                notices.Add(snapshot.HaloUnavailableReason ?? "HaloPSA isn't connected, so the Halo client column was ignored.");
            }

            return new UsableColumns(
                permissions.CanEditDomains && columns.Contains(ImportColumn.Groups),
                permissions.CanEditDomains && columns.Contains(ImportColumn.Tags),
                haloColumnUsable && snapshot.HaloClients is not null,
                permissions.CanEditDomains && columns.Contains(ImportColumn.Monitored),
                permissions.CanEditDomains && columns.Contains(ImportColumn.DkimSelectors),
                permissions.CanManageMtaSts && hasMtaStsColumns);
        }

        private static string JoinLabels(IReadOnlyList<string> labels) =>
            labels.Count == 1 ? labels[0] : string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels[^1];
    }

    /// <summary>The first row for a domain, with any later rows for it merged in: names combined, and for other values
    /// the later non-blank one wins.</summary>
    private sealed class MergedRow(ImportTableRow first, string domain)
    {
        public int LineNumber { get; } = first.LineNumber;
        public string RawDomain { get; } = first.RawDomain;
        public string Domain { get; } = domain;
        public NameListCell? Groups { get; private set; } = first.Groups;
        public NameListCell? Tags { get; private set; } = first.Tags;
        public string? HaloClient { get; private set; } = first.HaloClient;
        public bool? Monitored { get; private set; } = first.Monitored;
        public IReadOnlyList<string>? DkimSelectors { get; private set; } = first.DkimSelectors;
        public MtaStsImportMode? MtaStsMode { get; private set; } = first.MtaStsMode;
        public IReadOnlyList<string>? MtaStsMxHosts { get; private set; } = first.MtaStsMxHosts;
        public int? MtaStsMaxAgeSeconds { get; private set; } = first.MtaStsMaxAgeSeconds;
        public List<string> Notes { get; } = [.. first.Problems];

        public void Merge(ImportTableRow later)
        {
            Groups = NameListCell.Combine(Groups, later.Groups);
            Tags = NameListCell.Combine(Tags, later.Tags);
            HaloClient = later.HaloClient ?? HaloClient;
            Monitored = later.Monitored ?? Monitored;
            DkimSelectors = later.DkimSelectors ?? DkimSelectors;
            MtaStsMode = later.MtaStsMode ?? MtaStsMode;
            MtaStsMxHosts = later.MtaStsMxHosts ?? MtaStsMxHosts;
            MtaStsMaxAgeSeconds = later.MtaStsMaxAgeSeconds ?? MtaStsMaxAgeSeconds;
            Notes.AddRange(later.Problems.Select(problem => $"Line {later.LineNumber}: {problem}"));
        }
    }

    /// <summary>Tracks the unknown group, tag and Halo client names, and resolves any name to the one to use.</summary>
    private sealed class NameResolver(ImportSnapshot snapshot, ImportPermissions permissions, IReadOnlyDictionary<NameKey, NameResolution> chosen)
    {
        private readonly Dictionary<NameKey, (string Name, List<int> Lines)> _unknown = [];

        public IReadOnlyList<string> Existing(ImportNameKind kind) => kind switch
        {
            ImportNameKind.Group => snapshot.GroupNames,
            ImportNameKind.Tag => snapshot.TagNames,
            _ => snapshot.HaloClients?.Select(client => client.Name).ToList() ?? []
        };

        private bool CanCreate(ImportNameKind kind) => kind switch
        {
            ImportNameKind.Group => permissions.CanAddGroups,
            ImportNameKind.Tag => permissions.CanAddTags,
            _ => false
        };

        public void Consider(ImportNameKind kind, string name, int lineNumber)
        {
            if (NameMatcher.FindExisting(name, Existing(kind)) is not null)
            {
                return;
            }

            var key = new NameKey(kind, name.ToLowerInvariant());
            if (!_unknown.TryGetValue(key, out var entry))
            {
                entry = (name, []);
                _unknown[key] = entry;
            }

            if (!entry.Lines.Contains(lineNumber))
            {
                entry.Lines.Add(lineNumber);
            }
        }

        public IReadOnlyList<UnknownName> UnknownNames() =>
            _unknown.Select(pair => new UnknownName(pair.Key.Kind, pair.Value.Name, pair.Value.Lines,
                    NameMatcher.Suggest(pair.Value.Name, Existing(pair.Key.Kind)), CanCreate(pair.Key.Kind), Effective(pair.Key, pair.Value.Name)))
                .OrderBy(name => name.Kind).ThenBy(name => name.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public IReadOnlyList<string> ToCreate(ImportNameKind kind) =>
            _unknown.Where(pair => pair.Key.Kind == kind && Effective(pair.Key, pair.Value.Name).Choice == NameChoice.Create)
                .Select(pair => pair.Value.Name.Trim())
                .ToList();

        /// <summary>The name to use: the existing one (ignoring case), the mapped one, the name itself if it will be
        /// created, or null if it's left out.</summary>
        public string? Resolve(ImportNameKind kind, string name)
        {
            var existing = Existing(kind);
            if (NameMatcher.FindExisting(name, existing) is { } known)
            {
                return known;
            }

            var resolution = Effective(new NameKey(kind, name.ToLowerInvariant()), name);
            return resolution.Choice switch
            {
                NameChoice.Create => name.Trim(),
                NameChoice.MapTo => NameMatcher.FindExisting(resolution.MapTo!, existing),
                _ => null
            };
        }

        private NameResolution Effective(NameKey key, string name)
        {
            var existing = Existing(key.Kind);
            if (chosen.TryGetValue(key, out var choice)
                && (choice.Choice == NameChoice.LeaveOut
                    || (choice.Choice == NameChoice.Create && CanCreate(key.Kind))
                    || (choice.Choice == NameChoice.MapTo && choice.MapTo is not null && NameMatcher.FindExisting(choice.MapTo, existing) is not null)))
            {
                return choice;
            }

            // Only the same name written differently is mapped for you. A near-miss typo might be a genuinely different
            // name ("Client C" and "Client A"), so it is suggested and the person picks it.
            var sameName = existing.FirstOrDefault(candidate => NameMatcher.IsSameName(name, candidate));
            return sameName is not null ? new NameResolution(NameChoice.MapTo, sameName)
                : CanCreate(key.Kind) ? new NameResolution(NameChoice.Create)
                : new NameResolution(NameChoice.LeaveOut);
        }
    }

    /// <summary>Plans one domain: its target, and the changes, removals and notes the preview shows.</summary>
    private sealed class RowPlanner(MergedRow row, ExistingDomain? existing, ImportSnapshot snapshot, ExistingDomainMode mode,
        UsableColumns usable, IReadOnlySet<ImportColumn> columns, NameResolver names)
    {
        private readonly AuditChanges _changes = new();
        private readonly List<string> _removals = [];
        private readonly List<string> _notes = [.. row.Notes];

        public PlannedRow Plan()
        {
            var status = existing is null ? ImportRowStatus.New : ImportRowStatus.AlreadyMonitored;
            if (existing is not null && mode == ExistingDomainMode.Skip)
            {
                _notes.Add("Already monitored, so it's skipped.");
                return Result(status, null);
            }

            var groups = usable.Groups ? PlanNames(ImportNameKind.Group, "Groups", "group", row.Groups, existing?.Groups, columns.Contains(ImportColumn.Groups)) : null;
            var tags = usable.Tags ? PlanNames(ImportNameKind.Tag, "Tags", "tag", row.Tags, existing?.Tags, columns.Contains(ImportColumn.Tags)) : null;
            var (setHaloClient, haloClientId) = PlanHaloClient();
            var monitored = PlanMonitored();
            var dkimSelectors = PlanDkimSelectors();
            var mtaSts = PlanMtaSts();

            var target = new DomainTarget(groups, tags, setHaloClient, haloClientId, monitored, dkimSelectors, mtaSts);
            if (existing is not null && !_changes.Any)
            {
                _notes.Add("Already monitored, and there's nothing to change.");
                return Result(status, null);
            }

            return Result(status, target);
        }

        private PlannedRow Result(ImportRowStatus status, DomainTarget? target) =>
            new(row.LineNumber, row.RawDomain, row.Domain, status, null, existing?.Id, null, target, _changes.Items, _removals, _notes);

        private NameSetChange? PlanNames(ImportNameKind kind, string label, string noun, NameListCell? cell, IReadOnlyList<string>? current, bool columnPresent)
        {
            var isMatch = mode == ExistingDomainMode.Match && current is not null;
            if (cell is null && !(isMatch && columnPresent))
            {
                return null;
            }

            var add = (cell?.Names ?? []).Select(name => names.Resolve(kind, name)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var remove = new List<string>();
            if (cell is { Removals.Count: > 0 })
            {
                if (current is null)
                {
                    _notes.Add($"Entries starting with - are ignored for a new domain: it has no {noun}s to remove.");
                }
                else if (isMatch)
                {
                    _notes.Add("Entries starting with - are ignored when matching the file.");
                }
                else
                {
                    foreach (var removal in cell.Removals)
                    {
                        if (NameMatcher.FindExisting(removal, current) is { } found)
                        {
                            remove.Add(found);
                        }
                        else
                        {
                            _notes.Add($"\"{removal}\" isn't one of this domain's {noun}s, so there was nothing to remove.");
                        }
                    }
                }
            }

            var change = new NameSetChange(add, remove, isMatch);
            var before = current ?? [];
            var after = change.ApplyTo(before);
            _changes.Set(label, before, after);
            _removals.AddRange(before.Where(name => !after.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(name => $"{char.ToUpperInvariant(noun[0])}{noun[1..]} \"{name}\" removed"));
            return change;
        }

        private (bool SetHaloClient, int? HaloClientId) PlanHaloClient()
        {
            if (!usable.HaloClient || row.HaloClient is not { } requestedName)
            {
                return (false, null);
            }

            if (names.Resolve(ImportNameKind.HaloClient, requestedName) is not { } resolvedName)
            {
                _notes.Add($"Halo client \"{requestedName}\" was left out.");
                return (false, null);
            }

            var client = snapshot.HaloClients!.First(candidate => string.Equals(candidate.Name, resolvedName, StringComparison.OrdinalIgnoreCase));
            _changes.Field("Halo client", ClientName(existing?.HaloClientId), client.Name);
            return (true, client.Id);
        }

        private string? ClientName(int? clientId) =>
            clientId is null ? null : snapshot.HaloClients?.FirstOrDefault(client => client.Id == clientId)?.Name ?? $"Halo client {clientId}";

        private bool? PlanMonitored()
        {
            if (!usable.Monitored || row.Monitored is not { } monitored)
            {
                return null;
            }

            // A domain added by the import starts monitored.
            var before = existing?.IsMonitored ?? true;
            _changes.Field("Monitored", before, monitored);
            if (existing is not null && before && !monitored)
            {
                _removals.Add("Monitoring turned off");
            }

            return monitored;
        }

        private IReadOnlyList<string>? PlanDkimSelectors()
        {
            if (!usable.DkimSelectors || row.DkimSelectors is not { } selectors)
            {
                return null;
            }

            var before = existing?.DkimSelectors ?? [];
            _changes.Set("DKIM selectors", before, selectors);
            _removals.AddRange(before.Where(selector => !selectors.Contains(selector, StringComparer.OrdinalIgnoreCase)).Select(selector => $"DKIM selector \"{selector}\" removed"));
            return selectors;
        }

        private MtaStsTarget? PlanMtaSts()
        {
            if (!usable.MtaSts || (row.MtaStsMode is null && row.MtaStsMxHosts is null && row.MtaStsMaxAgeSeconds is null))
            {
                return null;
            }

            var currentEnabled = existing?.MtaStsEnabled ?? false;
            var currentMode = existing?.MtaStsMode ?? MtaStsMode.Testing;
            var currentMxHosts = existing?.MtaStsMxHosts ?? [];
            var currentMaxAge = existing?.MtaStsMaxAgeSeconds ?? DefaultMaxAgeSeconds;

            var enabled = row.MtaStsMode is { } requested ? requested != MtaStsImportMode.Off : currentEnabled;
            var modeAfter = row.MtaStsMode switch
            {
                MtaStsImportMode.None => MtaStsMode.None,
                MtaStsImportMode.Testing => MtaStsMode.Testing,
                MtaStsImportMode.Enforce => MtaStsMode.Enforce,
                _ => currentMode
            };

            var lookedUp = row.MtaStsMxHosts is null && currentMxHosts.Count == 0
                ? snapshot.LookedUpMxHosts.GetValueOrDefault(row.Domain) ?? []
                : [];
            var mxHosts = row.MtaStsMxHosts ?? (currentMxHosts.Count > 0 ? currentMxHosts : lookedUp);
            var maxAge = row.MtaStsMaxAgeSeconds ?? currentMaxAge;

            if (enabled && !currentEnabled && mxHosts.Count == 0)
            {
                _notes.Add("MTA-STS wasn't turned on: no MX hosts were given, and none were found in DNS.");
                return null;
            }

            _changes
                .Field("MTA-STS", Describe(currentEnabled, currentMode), Describe(enabled, modeAfter))
                .Set("MTA-STS MX hosts", currentMxHosts, mxHosts)
                .Field("MTA-STS max age (seconds)", currentMaxAge, maxAge);
            if (currentEnabled && !enabled)
            {
                _removals.Add("MTA-STS turned off");
            }

            if (lookedUp.Count > 0 && ReferenceEquals(mxHosts, lookedUp))
            {
                _notes.Add($"MX hosts found in DNS: {string.Join(", ", mxHosts)}.");
            }

            return new MtaStsTarget(enabled, modeAfter, mxHosts, maxAge);
        }

        private static string Describe(bool enabled, MtaStsMode mode) => enabled ? mode.ToString() : "Off";
    }
}
