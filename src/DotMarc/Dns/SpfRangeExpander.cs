using System.Text.Json;
using DotMarc.Reporting;

namespace DotMarc.Dns;

/// <summary>One IP range a domain's SPF record authorises. <see cref="Include"/> is the host of the top-level include it was
/// found under (the domain itself for the domain's own <c>ip4:</c> and <c>ip6:</c> terms) and <see cref="Service"/> is the
/// catalogue service that include belongs to, null when none does.</summary>
public sealed record SpfServiceRange(string? Service, string Include, string Cidr);

public static class SpfServiceRanges
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<SpfServiceRange> ranges) => JsonSerializer.Serialize(ranges, JsonOptions);

    /// <summary>The stored ranges. Missing or unreadable JSON gives an empty list rather than an error, so a bad value never
    /// breaks a page.</summary>
    public static IReadOnlyList<SpfServiceRange> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SpfServiceRange>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Reads an SPF record the way a receiver would to find the IP ranges it authorises, following include and redirect
/// into their targets. It reads, not evaluates, so its limits are looser than SPF's own: <see cref="MaxLookups"/> lookups and
/// <see cref="MaxDepth"/> levels, with a path set so a loop ends. Only terms that authorise (the plus qualifier) count.</summary>
public sealed class SpfRangeExpander(ITxtRecordLookup lookup)
{
    public const int MaxLookups = 50;
    public const int MaxDepth = 10;

    /// <summary>The ranges, or null when a lookup failed (the caller should keep what it had before).</summary>
    public async Task<IReadOnlyList<SpfServiceRange>?> ExpandAsync(string domainName, SpfRecord record, CancellationToken cancellationToken)
    {
        var state = new ExpandState();
        var domainKey = domainName.TrimEnd('.').ToLowerInvariant();
        state.Path.Add(domainKey);
        try
        {
            await WalkAsync(record, domainName, [], state, depth: 1, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return state.Ranges;
    }

    /// <param name="topLevelInclude">The include the walk entered the domain's record through, or the domain's own name.</param>
    /// <param name="hostsOnPath">The hosts from that include down to the record being read, outermost first.</param>
    private async Task WalkAsync(SpfRecord record, string topLevelInclude, List<string> hostsOnPath, ExpandState state, int depth, CancellationToken cancellationToken)
    {
        foreach (var term in record.Terms)
        {
            if (term.Kind == SpfTermKind.Mechanism && term.Qualifier == '+'
                && (term.Name.Equals("ip4", StringComparison.OrdinalIgnoreCase) || term.Name.Equals("ip6", StringComparison.OrdinalIgnoreCase))
                && term.Argument.StartsWith(':')
                && CidrRange.TryParse(term.Argument[1..], out var range)
                && IsFamily(term.Name, range))
            {
                state.Add(new SpfServiceRange(ServiceOnPath(hostsOnPath), topLevelInclude, range.ToString()));
            }
            else if ((term.IsInclude && term.Qualifier == '+') || term.IsRedirect)
            {
                await FollowAsync(term, topLevelInclude, hostsOnPath, state, depth, cancellationToken).ConfigureAwait(false);
            }

            if (state.Lookups >= MaxLookups)
            {
                return;
            }
        }
    }

    private async Task FollowAsync(SpfTerm term, string topLevelInclude, List<string> hostsOnPath, ExpandState state, int depth, CancellationToken cancellationToken)
    {
        if (term.Target is not { Length: > 0 } target || target.Contains('%') || depth > MaxDepth)
        {
            return;
        }

        var key = target.TrimEnd('.').ToLowerInvariant();
        if (state.Path.Contains(key) || state.Lookups >= MaxLookups)
        {
            return;
        }

        state.Lookups++;
        var records = (await lookup.GetTxtValuesAsync(target, cancellationToken).ConfigureAwait(false)).Where(SpfRecord.IsSpf).ToList();
        if (records.Count == 0)
        {
            return;
        }

        // A redirect or include straight from the domain's own record starts a new top-level include.
        var entersFromDomain = hostsOnPath.Count == 0;
        var nextTop = entersFromDomain ? target.TrimEnd('.') : topLevelInclude;
        List<string> nextHosts = [.. hostsOnPath, target];

        state.Path.Add(key);
        await WalkAsync(SpfRecord.Parse(records[0]), nextTop, nextHosts, state, depth + 1, cancellationToken).ConfigureAwait(false);
        state.Path.Remove(key);
    }

    private static string? ServiceOnPath(List<string> hostsOnPath) =>
        hostsOnPath.Select(SenderServiceCatalog.ServiceForSpfInclude).FirstOrDefault(service => service is not null);

    private static bool IsFamily(string mechanism, CidrRange range) =>
        mechanism.Equals("ip4", StringComparison.OrdinalIgnoreCase)
            ? range.Network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            : range.Network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    private sealed class ExpandState
    {
        private readonly HashSet<(string Include, string Cidr)> _seen = [];

        public int Lookups { get; set; }
        public HashSet<string> Path { get; } = [];
        public List<SpfServiceRange> Ranges { get; } = [];

        public void Add(SpfServiceRange range)
        {
            if (_seen.Add((range.Include, range.Cidr)))
            {
                Ranges.Add(range);
            }
        }
    }
}
