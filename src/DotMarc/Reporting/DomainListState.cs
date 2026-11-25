using System.Globalization;
using System.Text.Json;
using DotMarc.Baselines;
using DotMarc.Psa;
using DotMarc.Notifications;
using Microsoft.AspNetCore.WebUtilities;

namespace DotMarc.Reporting;

public sealed record DomainListState
{
    public bool GroupedView { get; init; }
    public bool ShowFilters { get; init; }
    public int? GroupId { get; init; }
    public bool AllMyGroups { get; init; }
    public int? TagId { get; init; }
    public string? Search { get; init; }
    public string? Status { get; init; }
    public double? MinPassRatePercent { get; init; }
    public string? LastReport { get; init; }
    public bool? Monitored { get; init; }
    public DmarcTargetOutcome? Target { get; init; }
    public string? Dmarc { get; init; }
    public string? MtaSts { get; init; }
    public string? Dkim { get; init; }
    public int? MinReportCount { get; init; }
    public Dictionary<PsaKind, string>? Companies { get; init; }
    public string? Sort { get; init; }
    public bool Descending { get; init; }

    public static readonly string[] SortKeys = ["name", "status", "pass", "last", "monitored", "target", "dmarc", "mta", "dkim", "reports", "groups", "tags"];

    public IReadOnlyList<string> ActiveFilters
    {
        get
        {
            var labels = new List<string>();
            if (GroupId is not null) labels.Add("Group");
            if (TagId is not null) labels.Add("Tag");
            if (!string.IsNullOrWhiteSpace(Search)) labels.Add($"Domain: {Search}");
            if (Status is not null) labels.Add($"Report status: {Status}");
            if (MinPassRatePercent is not null) labels.Add($"Pass rate >= {MinPassRatePercent}%");
            if (!string.IsNullOrWhiteSpace(LastReport)) labels.Add($"Last report: {LastReport}");
            if (Monitored is not null) labels.Add($"Monitored: {Monitored}");
            if (Target is not null) labels.Add($"Target: {Target}");
            if (Dmarc is not null) labels.Add($"DMARC: {Dmarc}");
            if (MtaSts is not null) labels.Add($"MTA-STS: {MtaSts}");
            if (Dkim is not null) labels.Add($"DKIM: {Dkim}");
            if (MinReportCount is not null) labels.Add($"Reports >= {MinReportCount}");
            if (Companies is not null) labels.AddRange(Companies.Select(pair => $"{pair.Key.DisplayName()} company: {pair.Value}"));
            return labels;
        }
    }

    public bool Matches(DashboardDomainRow row, IReadOnlyCollection<int> groups, IReadOnlyCollection<int> tags, int reportCount, IReadOnlyCollection<PsaCompanyLink> links) =>
        (GroupId is null || groups.Contains(GroupId.Value))
        && (TagId is null || tags.Contains(TagId.Value))
        && (string.IsNullOrWhiteSpace(Search) || row.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))
        && (Status is null || row.Status == Status)
        && (MinPassRatePercent is null || (row.PassRate ?? 0) * 100 >= MinPassRatePercent)
        && (string.IsNullOrWhiteSpace(LastReport) || (row.LastReportReceivedUtc?.ToString("g") ?? "never").Contains(LastReport, StringComparison.OrdinalIgnoreCase))
        && (Monitored is null || row.IsMonitored == Monitored)
        && (Target is null || row.IsMonitored && row.TargetOutcome == Target)
        && (Dmarc is null || DmarcStatusPresentation.GetLabel(row.DmarcCheckStatus, row.DmarcAuthorizationCheckStatus) == Dmarc)
        && (MtaSts is null || MtaStsStatusPresentation.GetLabel(row.MtaStsStatus) == MtaSts)
        && (Dkim is null || DkimStatusPresentation.GetLabel(row.DkimCheckStatus) == Dkim)
        && (MinReportCount is null || reportCount >= MinReportCount)
        && (Companies is null || Companies.All(pair => links.Any(link => link.Psa == pair.Key && link.CompanyId == pair.Value)));

    public IEnumerable<DashboardDomainRow> Order(IEnumerable<DashboardDomainRow> rows, Func<DashboardDomainRow, IComparable>? managementKey = null)
    {
        if (Sort is null) return rows;
        Func<DashboardDomainRow, IComparable> key = Sort switch
        {
            "status" => row => row.Status,
            "pass" => row => row.PassRate ?? -1,
            "last" => row => row.LastReportReceivedUtc ?? DateTimeOffset.MinValue,
            "monitored" => row => row.IsMonitored,
            "target" => row => row.TargetOutcome,
            "dmarc" => row => DmarcStatusPresentation.GetLabel(row.DmarcCheckStatus, row.DmarcAuthorizationCheckStatus),
            "mta" => row => MtaStsStatusPresentation.GetLabel(row.MtaStsStatus),
            "dkim" => row => DkimStatusPresentation.GetLabel(row.DkimCheckStatus),
            "name" => row => row.Name,
            "reports" or "groups" or "tags" => managementKey ?? throw new InvalidOperationException("Management sorting requires its row projection."),
            _ => throw new InvalidOperationException("Unrecognised domain sort."),
        };
        return (Descending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ThenBy(row => row.Id);
    }

    public DomainListState AsDefault() => this with { Search = null, LastReport = null };

    public void Validate()
    {
        if (GroupId is <= 0 || TagId is <= 0 || MinPassRatePercent is < 0 or > 100
            || MinPassRatePercent is { } percent && !double.IsFinite(percent)
            || MinReportCount is < 0 || Sort is not null && !SortKeys.Contains(Sort)
            || Status is not null and not ("OK" or "Warning" or "Missing")
            || Target is not null && Target is not (DmarcTargetOutcome.Below or DmarcTargetOutcome.NeedsChecking or DmarcTargetOutcome.Meets)
            || Companies?.Any(pair => !Enum.IsDefined(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) == true
            || Dmarc is not null && !Enum.GetValues<Data.DmarcCheckStatus>().SelectMany(status => Enum.GetValues<Data.DmarcAuthorizationCheckStatus>().Select(authorization => DmarcStatusPresentation.GetLabel(status, authorization))).Contains(Dmarc)
            || MtaSts is not null && !Enum.GetValues<Data.MtaStsStatus>().Select(MtaStsStatusPresentation.GetLabel).Contains(MtaSts)
            || Dkim is not null && !Enum.GetValues<Data.DkimCheckStatus>().Select(DkimStatusPresentation.GetLabel).Contains(Dkim))
            throw new FormatException("Invalid domain filters.");
    }

    public string ToUrl(string route)
    {
        var values = new Dictionary<string, string?> { ["filters"] = "1" };
        void Set(string key, object? value) { if (value is not null) values[key] = Convert.ToString(value, CultureInfo.InvariantCulture); }
        Set("group", AllMyGroups ? "all" : GroupId);
        Set("tag", TagId); Set("search", Search); Set("status", Status); Set("minPass", MinPassRatePercent);
        Set("last", LastReport); Set("monitored", Monitored); Set("target", TargetSlug(Target));
        Set("dmarc", Dmarc); Set("mta", MtaSts); Set("dkim", Dkim); Set("minReports", MinReportCount);
        if (GroupedView) Set("view", "group");
        if (ShowFilters) Set("open", "true");
        Set("sort", Sort);
        if (Descending) Set("desc", "true");
        if (Companies is not null)
            foreach (var (psa, company) in Companies) Set($"company.{psa}", company);
        return QueryHelpers.AddQueryString(route, values);
    }

    public static string? TargetSlug(DmarcTargetOutcome? target) => target switch
    {
        DmarcTargetOutcome.Below => "below", DmarcTargetOutcome.NeedsChecking => "checking", DmarcTargetOutcome.Meets => "meets", _ => null
    };

    public static DomainListState? FromQuery(Uri uri)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.Count == 0 || query.Keys.All(key => key == "dnsPush")) return null;
        var allowed = new[] { "filters", "group", "tag", "search", "status", "minPass", "last", "monitored", "target", "dmarc", "mta", "dkim", "minReports", "view", "open", "sort", "desc" };
        if (query.Any(pair => pair.Value.Count != 1 || !allowed.Contains(pair.Key) && !pair.Key.StartsWith("company.", StringComparison.Ordinal)))
            throw new FormatException("Unrecognised domain filter.");
        string? Read(string key) => query.TryGetValue(key, out var value) ? value.ToString() : null;
        int? Number(string key) => Read(key) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : null;
        bool? Flag(string key) => Read(key) is { } value ? bool.Parse(value) : null;
        var companies = new Dictionary<PsaKind, string>();
        foreach (var key in query.Keys.Where(key => key.StartsWith("company.", StringComparison.Ordinal)))
        {
            if (!Enum.TryParse<PsaKind>(key[8..], out var psa) || !Enum.IsDefined(psa)) throw new FormatException("Invalid PSA filter.");
            companies[psa] = Read(key)!;
        }
        var target = Read("target") switch { null => (DmarcTargetOutcome?)null, "below" => DmarcTargetOutcome.Below, "checking" => DmarcTargetOutcome.NeedsChecking, "meets" => DmarcTargetOutcome.Meets, _ => throw new FormatException("Invalid target filter.") };
        if (Read("view") is not null and not "group") throw new FormatException("Invalid view.");
        var result = new DomainListState
        {
            GroupId = Read("group") == "all" ? null : Number("group"), AllMyGroups = Read("group") == "all" || target is not null && !query.ContainsKey("group"),
            TagId = Number("tag"), Search = Read("search"), Status = Read("status"),
            MinPassRatePercent = Read("minPass") is { } percent ? double.Parse(percent, CultureInfo.InvariantCulture) : null,
            LastReport = Read("last"), Monitored = Flag("monitored"), Target = target, Dmarc = Read("dmarc"), MtaSts = Read("mta"), Dkim = Read("dkim"),
            MinReportCount = Number("minReports"), GroupedView = Read("view") == "group", ShowFilters = Flag("open") == true,
            Sort = Read("sort"), Descending = Flag("desc") == true, Companies = companies.Count == 0 ? null : companies,
        };
        result.Validate();
        return result;
    }
}

public sealed record DomainListDefaults(int Version = 1, DomainListState? Inventory = null, DomainListState? Dashboard = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DomainListDefaults Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("version", out var version))
            {
                if (version.GetInt32() != 1) throw new FormatException("Unsupported defaults version.");
                var result = JsonSerializer.Deserialize<DomainListDefaults>(json, JsonOptions) ?? throw new FormatException("Empty defaults.");
                result.Inventory?.Validate();
                result.Dashboard?.Validate();
                return result;
            }
            var state = JsonSerializer.Deserialize<DomainListState>(json, JsonOptions) ?? throw new FormatException("Empty defaults.");
            state.Validate();
            return new(Inventory: state, Dashboard: state with { GroupedView = false });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or OverflowException)
        {
            throw new FormatException("Could not read saved domain defaults.", exception);
        }
    }
}
