using DotMarc.Components.Dialogs;
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.IpEnrichment;
using DotMarc.Notifications;
using DotMarc.Portal;
using DotMarc.Psa;
using DotMarc.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using MudBlazor;

namespace DotMarc.Components.Shared;

public partial class DomainWorkspace : IAsyncDisposable
{
    [Parameter] public bool Inventory { get; set; }
    [Inject] private IDbContextFactory<DotMarcDbContext> DbFactory { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private IAuthorizationService Authorization { get; set; } = null!;
    [Inject] private Audit.AuditActorAccessor Actor { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private PsaDirectory Directory { get; set; } = null!;
    [Inject] private ILogger<DomainWorkspace> Logger { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private TimeProvider Clock { get; set; } = null!;

    private DomainListState _state = new();
    private string? _userKey, _error, _addError;
    private string _newDomain = "", _lastUri = "";
    private bool _loaded, _hasDefault, _management, _canReorder, _disposed, _restoreScroll, _memoryAttempted, _memoryFailed, _scrollTop;
    private int? _draggedId;
    private int _loadVersion;
    private bool _loading;
    private bool _focusAddDomain, _adding;
    private IReadOnlyCollection<int> _scope = [];
    private List<Group> _groups = [];
    private List<Tag> _tags = [];
    private List<Domain> _domains = [];
    private Dictionary<int, Domain> _domainsById = [];
    private Dictionary<int, int> _reportCounts = [];
    private List<DashboardDomainRow> _allRows = [], _rows = [];
    private IReadOnlyList<DashboardDomainGroup> _grouped = [];
    private DashboardActionCentre? _actionCentre;
    private Dictionary<int, int> _senderReviews = [];
    private DateTimeOffset _snapshotUtc;
    private DnsRecordSettings _settings = new();
    private int _parseFailures;
    private IReadOnlyList<PsaCompanyList> _psaLists = [];
    private Dictionary<PsaKind, List<SelectOption<string>>> _companyOptions = [];
    private IJSObjectReference? _memory;
    private bool CanReorder => _canReorder && _scope.Count == 0 && !_state.GroupedView && _state.Sort is null && _state.ActiveFilters.Count == 0;
    private string Route => Inventory ? "/domains" : "/dashboard";
    private static readonly KeyValuePair<string, string>[] Columns =
    [
        new("name", "Domain"), new("status", "Report status"), new("pass", "Pass rate (30d)"), new("last", "Last report"),
        new("monitored", "Monitored"), new("target", "Target"), new("dmarc", "DMARC"), new("mta", "MTA-STS"), new("dkim", "DKIM")
    ];

    protected override async Task OnInitializedAsync()
    {
        _userKey = DashboardPreferencesStore.UserKeyOf(await Actor.GetAsync());
        await ReadDefaultAsync();
        ReadQuery();
        _lastUri = Navigation.Uri;
        _focusAddDomain = Inventory && new Uri(Navigation.Uri).Fragment == "#add-domain";
        Navigation.LocationChanged += LocationChanged;
        await LoadAsync();
    }

    private async Task ReadDefaultAsync()
    {
        _state = new();
        if (_userKey is not null)
        {
            try
            {
                await using var db = await DbFactory.CreateDbContextAsync();
                var defaults = await DashboardPreferencesStore.GetListsAsync(db, _userKey);
                var saved = Inventory ? defaults.Inventory : defaults.Dashboard;
                _hasDefault = saved is not null;
                _state = saved ?? new();
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                Logger.LogWarning(exception, "Could not load domain list defaults");
                Snackbar.Add("Your saved defaults could not be loaded. They have not been overwritten.", Severity.Warning);
            }
        }
    }

    private void ReadQuery()
    {
        try
        {
            var linked = DomainListState.FromQuery(new Uri(Navigation.Uri));
            if (!Inventory && linked?.Target is not null)
            {
                Navigation.NavigateTo(linked.ToUrl("/domains"), replace: true);
                return;
            }
            if (!Inventory && linked is not null && (linked.MinReportCount is not null || linked.Companies is not null || linked.Sort is "reports" or "groups" or "tags"))
                throw new FormatException("Management filters belong to Inventory.");
            if (linked is not null) _state = linked with { ShowFilters = linked.ShowFilters || linked.Target is not null };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            Snackbar.Add("This link contains invalid filters. No saved filters were applied.", Severity.Warning);
            _state = new() { ShowFilters = true };
        }
        if (new Uri(Navigation.Uri).Query.Contains("dnsPush=invalid", StringComparison.Ordinal))
            Snackbar.Add("That DNS push link was invalid or has expired. Try again from the domain's page.", Severity.Warning);
    }

    private void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (new Uri(args.Location).AbsolutePath != Route || args.Location == _lastUri || _disposed) return;
        _ = InvokeAsync(async () =>
        {
            _lastUri = args.Location;
            _focusAddDomain = Inventory && new Uri(args.Location).Fragment == "#add-domain";
            await ReadDefaultAsync();
            ReadQuery();
            _scrollTop = true;
            await LoadAsync();
            StateHasChanged();
        });
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _loading = true;
        try
        {
            var user = (await Authentication.GetAuthenticationStateAsync()).User;
            var scope = PortalData.ScopedGroupIds(user);
            var snapshotUtc = Clock.GetUtcNow();
            await using var db = await DbFactory.CreateDbContextAsync();
            var groups = await db.Groups.AsNoTracking().Where(group => scope.Count == 0 || scope.Contains(group.Id)).OrderBy(group => group.Name).ToListAsync();
            var tags = await db.Tags.AsNoTracking().OrderBy(tag => tag.Name).ToListAsync();
            var cutoff = DomainStatistics.GetWindowCutoffUtc(snapshotUtc);
            var domains = await db.Domains.AsNoTracking().Where(domain => scope.Count == 0 || domain.Groups.Any(group => scope.Contains(group.Id)))
                .Include(domain => domain.Groups).Include(domain => domain.Tags).Include(domain => domain.PsaCompanyLinks)
                .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.OverrideReasons)
                .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.AuthDetails)
                .AsSplitQuery().OrderBy(domain => domain.SortOrder).ThenBy(domain => domain.Name).ToListAsync();
            var ids = domains.Select(domain => domain.Id).ToList();
            var counts = await db.Reports.Where(report => ids.Contains(report.DomainId)).GroupBy(report => report.DomainId).ToDictionaryAsync(group => group.Key, group => group.Count());
            var settings = await DnsRecordSettingsService.GetAsync(db);
            var parseFailures = (await Authorization.AuthorizeAsync(user, "DomainsWrite")).Succeeded ? await db.ParseFailures.CountAsync() : 0;
            var canEdit = (await Authorization.AuthorizeAsync(user, nameof(Permission.DomainsEdit))).Succeeded;
            var senderReviews = new Dictionary<int, int>();
            if (!Inventory)
            {
                var groupIds = domains.SelectMany(domain => domain.Groups).Select(group => group.Id).Distinct().ToList();
                var decisions = await db.SenderDecisions.AsNoTracking().Where(decision =>
                    decision.Scope == SenderDecisionScope.Baseline
                    || decision.DomainId != null && ids.Contains(decision.DomainId.Value)
                    || decision.GroupId != null && groupIds.Contains(decision.GroupId.Value)).ToListAsync();
                var ips = domains.SelectMany(domain => domain.Reports).SelectMany(report => report.Records).Select(record => record.SourceIp).Distinct().ToList();
                var cached = await IpInfoService.GetCachedAsync(db, ips, CancellationToken.None);
                var ranges = await IpInfoService.GetCachedRangesAsync(db, CancellationToken.None);
                var owners = IpInfoService.OwnerByIp(cached, ranges, ips, snapshotUtc);
                foreach (var domain in domains)
                    senderReviews[domain.Id] = SenderDecisionSummaries.Summarise(
                        domain.Reports.Where(report => report.ReceivedUtc <= snapshotUtc).ToList(), owners, decisions,
                        domain.Id, domain.Groups.Select(group => group.Id).ToList(), SpfServiceRanges.Parse(domain.SpfRangesJson)).UnreviewedSenders;
            }
            IReadOnlyList<PsaCompanyList> lists = [];
            if (Inventory && canEdit) lists = await Directory.LoadAsync(db);
            if (_disposed || version != _loadVersion) return;
            _scope = scope; _groups = groups; _tags = tags; _domains = domains;
            _senderReviews = senderReviews; _snapshotUtc = snapshotUtc;
            _domainsById = domains.ToDictionary(domain => domain.Id); _reportCounts = counts; _settings = settings; _parseFailures = parseFailures;
            _canReorder = (await Authorization.AuthorizeAsync(user, nameof(Permission.DomainsReorder))).Succeeded;
            _psaLists = lists;
            _companyOptions = domains.SelectMany(domain => domain.PsaCompanyLinks).Select(link => link.Psa).Concat(lists.Select(list => list.Psa)).Distinct().ToDictionary(
                psa => psa, psa => domains.SelectMany(domain => domain.PsaCompanyLinks).Where(link => link.Psa == psa).Select(link => new PsaCompany(link.CompanyId, link.CompanyName))
                    .Concat(lists.FirstOrDefault(list => list.Psa == psa)?.Companies ?? []).DistinctBy(company => company.Id).OrderBy(company => company.Name)
                    .Select(company => new SelectOption<string>(company.Id, company.Name)).ToList());
            NormaliseScope();
            Rebuild();
            _error = null; _loaded = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Npgsql.NpgsqlException)
        {
            Logger.LogError(exception, "Loading domain workspace failed");
            _error = "Could not load domains. Try again.";
        }
        finally
        {
            if (version == _loadVersion) _loading = false;
        }
    }

    private void NormaliseScope()
    {
        if (_state.TagId is { } tag && _tags.All(candidate => candidate.Id != tag))
        {
            _state = _state with { TagId = null };
            Snackbar.Add("A saved Tag no longer exists and was removed from the filters.", Severity.Warning);
        }
        if (_state.GroupId is { } group && _groups.All(candidate => candidate.Id != group))
        {
            _state = _state with { GroupId = null, AllMyGroups = false };
            Snackbar.Add("The selected Group is unavailable. Your current access scope is still enforced.", Severity.Warning);
        }
        if (_scope.Count == 1 || _scope.Count > 1 && !_state.AllMyGroups && _state.GroupId is null)
            _state = _state with { GroupId = _groups.FirstOrDefault()?.Id, AllMyGroups = false };
    }

    private List<int> VisibleGroups(Domain domain) => domain.Groups.Where(group => _groups.Any(visible => visible.Id == group.Id)).Select(group => group.Id).ToList();
    private void Rebuild()
    {
        (_, _allRows) = DashboardSummary.Build(_domains, _parseFailures, _settings);
        var matched = _allRows.Where(row =>
        {
            var domain = _domainsById[row.Id];
            return _state.Matches(row, VisibleGroups(domain), domain.Tags.Select(tag => tag.Id).ToList(), _reportCounts.GetValueOrDefault(row.Id), domain.PsaCompanyLinks);
        });
        int CountOf(DashboardDomainRow row) => _state.Sort switch
        {
            "groups" => VisibleGroups(_domainsById[row.Id]).Count,
            "tags" => _domainsById[row.Id].Tags.Count,
            _ => _reportCounts.GetValueOrDefault(row.Id)
        };
        _rows = _state.Order(matched, row => CountOf(row)).ToList();
        var ids = _rows.Select(row => row.Id).ToHashSet();
        if (!Inventory)
            _actionCentre = DashboardActionCentre.Build(_domains.Where(domain => ids.Contains(domain.Id)).ToList(), _senderReviews, _settings, _snapshotUtc);
        _grouped = DashboardGrouping.Group(_rows, _domains.ToDictionary(domain => domain.Id, domain => (IReadOnlyList<int>)VisibleGroups(domain)),
            _groups.Where(group => _state.GroupId is null || group.Id == _state.GroupId).Select(group => (group.Id, group.Name)).ToList());
    }

    private Task SortAsync(string key) => ChangeFiltersAsync(_state with
    {
        Sort = _state.Sort == key && _state.Descending ? null : key,
        Descending = _state.Sort == key && !_state.Descending
    });
    private string SortMark(string key) => _state.Sort == key ? (_state.Descending ? "(descending)" : "(ascending)") : "";

    private Task ChangeFiltersAsync(DomainListState value)
    {
        try { value.Validate(); }
        catch (FormatException)
        {
            Snackbar.Add("Enter valid domain filters (pass rate 0 to 100 and non-negative report counts).", Severity.Warning);
            return Task.CompletedTask;
        }
        _state = value; NormaliseScope(); Rebuild();
        var uri = _state.ToUrl(Route);
        _lastUri = Navigation.ToAbsoluteUri(uri).ToString();
        Navigation.NavigateTo(uri, new NavigationOptions { ReplaceHistoryEntry = true, HistoryEntryState = Navigation.HistoryEntryState });
        return Task.CompletedTask;
    }

    private async Task SaveDefaultAsync() => await WriteDefaultAsync(_state);
    private async Task ClearDefaultAsync() => await WriteDefaultAsync(null);
    private async Task WriteDefaultAsync(DomainListState? state)
    {
        if (_userKey is null) return;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await DashboardPreferencesStore.SaveListAsync(db, _userKey, Inventory, state);
            _hasDefault = state is not null;
            if (state is null) await ChangeFiltersAsync(new());
            Snackbar.Add(state is null ? "Default cleared for this page." : "Default saved for this page.", Severity.Success);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            Logger.LogWarning(exception, "Saving domain list defaults failed");
            Snackbar.Add("Could not save your default. Existing settings have not been replaced.", Severity.Error);
        }
    }

    private async Task<bool> MayWriteAsync(DotMarcDbContext db, Permission permission, int? id = null)
    {
        var user = (await Authentication.GetAuthenticationStateAsync()).User;
        var scope = PortalData.ScopedGroupIds(user);
        var allowed = (await Authorization.AuthorizeAsync(user, permission.ToString())).Succeeded && !ClientPortalGate.IsPortalUser(user);
        if (id is { } domainId)
            allowed &= await db.Domains.AnyAsync(domain => domain.Id == domainId && (scope.Count == 0 || domain.Groups.Any(group => scope.Contains(group.Id))));
        if (!allowed) Snackbar.Add("You no longer have permission to change that domain.", Severity.Warning);
        return allowed;
    }

    private async Task MutateAsync(int id, Permission permission, Func<DotMarcDbContext, Task> action)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            if (!await MayWriteAsync(db, permission, id)) return;
            await action(db);
            await LoadAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Npgsql.NpgsqlException or ArgumentException)
        {
            Logger.LogWarning(exception, "Changing domain {DomainId} failed", id);
            Snackbar.Add("Could not update the domain. Try again.", Severity.Error);
            await LoadAsync();
        }
    }

    private Task SetMonitoredAsync(int id, bool value) => MutateAsync(id, Permission.DomainsEdit,
        async db => await DomainManagementService.SetMonitoredAsync(db, await Actor.GetAsync(), id, value));
    private Task SetTagsAsync(int id, IEnumerable<int> ids) => MutateAsync(id, Permission.DomainsEdit,
        async db => await TagManagementService.SetDomainTagsAsync(db, await Actor.GetAsync(), id, ids.ToList()));
    private Task SetCompanyAsync(int id, PsaKind psa, PsaCompany? company) => MutateAsync(id, Permission.DomainsEdit,
        async db => await DomainManagementService.SetPsaCompanyAsync(db, await Actor.GetAsync(), id, psa, company));
    private Task SetGroupsAsync(int id, IEnumerable<int> ids) => MutateAsync(id, Permission.DomainsEdit, async db =>
    {
        var scope = PortalData.ScopedGroupIds((await Authentication.GetAuthenticationStateAsync()).User);
        var requested = ids.Distinct().ToList();
        if (scope.Count > 0 && (requested.Count == 0 || requested.Any(group => !scope.Contains(group))))
        {
            Snackbar.Add("Keep at least one of your groups. You can only assign your own groups.", Severity.Warning);
            return;
        }
        var hidden = await db.Domains.Where(domain => domain.Id == id).SelectMany(domain => domain.Groups).Where(group => scope.Count > 0 && !scope.Contains(group.Id)).Select(group => group.Id).ToListAsync();
        await GroupManagementService.SetDomainGroupsAsync(db, await Actor.GetAsync(), id, requested.Concat(hidden).Distinct().ToList());
    });

    private async Task AddAsync()
    {
        if (_adding) return;
        _adding = true;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            if (!await MayWriteAsync(db, Permission.DomainsAdd)) return;
            var scope = PortalData.ScopedGroupIds((await Authentication.GetAuthenticationStateAsync()).User);
            if (scope.Count > 0 && (_state.GroupId is not { } group || !scope.Contains(group)))
            {
                _addError = "Choose one of your Groups in Filters before adding a domain.";
                return;
            }
            if (_state.GroupId is not null && !(await Authorization.AuthorizeAsync((await Authentication.GetAuthenticationStateAsync()).User, nameof(Permission.DomainsEdit))).Succeeded)
            {
                _addError = "Assigning a Group while adding a domain also requires DomainsEdit. Clear the Group filter or ask an administrator.";
                return;
            }
            await using var transaction = await db.Database.BeginTransactionAsync();
            var result = await DomainManagementService.AddDomainAsync(db, await Actor.GetAsync(), _newDomain);
            _addError = result switch { DomainManagementService.AddDomainResult.InvalidName => "Enter a valid domain name.", DomainManagementService.AddDomainResult.AlreadyMonitored => "That domain already exists.", _ => null };
            if (result != DomainManagementService.AddDomainResult.Added) return;
            if (_state.GroupId is { } groupId)
            {
                DomainNameValidator.TryNormalize(_newDomain, out var name);
                var id = await db.Domains.Where(domain => domain.Name == name).Select(domain => domain.Id).SingleAsync();
                await GroupManagementService.SetDomainGroupsAsync(db, await Actor.GetAsync(), id, [groupId]);
            }
            await transaction.CommitAsync();
            _newDomain = ""; await LoadAsync();
            Snackbar.Add("Domain added. Active filters may hide it.", Severity.Success);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Npgsql.NpgsqlException)
        {
            Logger.LogWarning(exception, "Adding a domain failed");
            _addError = "Could not add the domain. Try again.";
        }
        finally { _adding = false; }
    }

    private async Task DeleteAsync(int id)
    {
        var domain = _domainsById[id];
        var dialog = await Dialogs.ShowAsync<ConfirmDeleteDomainDialog>("Remove domain", new DialogParameters<ConfirmDeleteDomainDialog>
        {
            { choice => choice.DomainName, domain.Name }, { choice => choice.ReportCount, _reportCounts.GetValueOrDefault(id) }
        });
        if (await dialog.Result is { Canceled: false })
            await MutateAsync(id, Permission.DomainsDelete, async db => await DomainManagementService.RemoveDomainAsync(db, await Actor.GetAsync(), id));
    }

    private async Task DropAsync(int target)
    {
        if (!CanReorder || _draggedId is not { } dragged || dragged == target) return;
        _draggedId = null;
        await using var db = await DbFactory.CreateDbContextAsync();
        if (!await MayWriteAsync(db, Permission.DomainsReorder)) return;
        var user = (await Authentication.GetAuthenticationStateAsync()).User;
        if (PortalData.ScopedGroupIds(user).Count != 0) { Snackbar.Add("Reordering requires unrestricted domain access.", Severity.Warning); return; }
        var ids = _rows.Select(row => row.Id).ToList();
        if (!ids.Remove(dragged) || !ids.Contains(target)) { Snackbar.Add("The list changed. Reload before reordering.", Severity.Warning); return; }
        ids.Insert(ids.IndexOf(target), dragged);
        try
        {
            var currentIds = await db.Domains.Select(domain => domain.Id).ToListAsync();
            if (!currentIds.ToHashSet().SetEquals(ids)) { Snackbar.Add("The list changed. Reload before reordering.", Severity.Warning); await LoadAsync(); return; }
            await DomainManagementService.ReorderAsync(db, await Actor.GetAsync(), ids);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Npgsql.NpgsqlException)
        {
            Logger.LogWarning(exception, "Reordering domains failed");
            Snackbar.Add("Could not save domain order.", Severity.Error);
        }
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!RendererInfo.IsInteractive || !_loaded || _memoryFailed) return;
        try
        {
            if (!_memoryAttempted)
            {
                _memoryAttempted = true;
                _memory = await JS.InvokeAsync<IJSObjectReference>("import", "./listPosition.js");
                var restored = await _memory.InvokeAsync<DomainListState?>("enter", Route, _userKey);
                if (restored is not null) { restored.Validate(); _state = restored; NormaliseScope(); Rebuild(); _restoreScroll = true; StateHasChanged(); return; }
            }
            if (_memory is null) return;
            await _memory.InvokeVoidAsync("update", _state);
            if (_restoreScroll) { _restoreScroll = false; await _memory.InvokeVoidAsync("restore"); }
            if (_scrollTop) { _scrollTop = false; await _memory.InvokeVoidAsync("top"); }
            if (_focusAddDomain)
            {
                _focusAddDomain = false;
                var user = (await Authentication.GetAuthenticationStateAsync()).User;
                if ((await Authorization.AuthorizeAsync(user, nameof(Permission.DomainsAdd))).Succeeded)
                    await _memory.InvokeVoidAsync("focusAddDomain");
            }
        }
        catch (Exception exception) when (exception is JSException or FormatException)
        {
            _memoryFailed = true;
            Logger.LogWarning(exception, "Restoring list position failed");
            Snackbar.Add("List position could not be restored. Your domain access is unchanged.", Severity.Warning);
        }
    }

    private static string TargetLabel(Baselines.DmarcTargetOutcome outcome) => outcome switch
    {
        Baselines.DmarcTargetOutcome.Below => "Below target", Baselines.DmarcTargetOutcome.NeedsChecking => "Needs checking",
        Baselines.DmarcTargetOutcome.Meets => "Meets target", _ => "No target"
    };

    public async ValueTask DisposeAsync()
    {
        _disposed = true; Navigation.LocationChanged -= LocationChanged;
        if (_memory is null) return;
        try { await _memory.InvokeVoidAsync("leave"); await _memory.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (JSException exception) { Logger.LogWarning(exception, "Saving list position on departure failed"); }
    }
}
