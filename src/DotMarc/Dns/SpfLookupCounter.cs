namespace DotMarc.Dns;

public sealed record SpfTermCost(string Term, int Lookups);

/// <summary>How many DNS lookups an SPF record needs. Receivers give up past <see cref="Limit"/>, and SPF then fails
/// for every message.</summary>
public sealed record SpfLookupCount(int Total, IReadOnlyList<SpfTermCost> TermCosts, IReadOnlyList<string> MissingTargets, IReadOnlyList<string> Problems)
{
    public const int Limit = 10;

    public bool IsOverLimit => Total > Limit;
}

/// <summary>Counts an SPF record's DNS lookups as RFC 7208 4.6.4 does: each include, a, mx, ptr, exists and redirect
/// costs one, and include and redirect are followed into their target's record.</summary>
public sealed class SpfLookupCounter(ITxtRecordLookup lookup)
{
    /// <summary>Past this the exact figure no longer matters, and a huge record shouldn't cost hundreds of queries.</summary>
    private const int StopCountingAt = 20;
    private const int MaxDepth = 10;
    private const int MaxVoidLookups = 2;

    public async Task<SpfLookupCount> CountAsync(string domainName, SpfRecord record, CancellationToken cancellationToken)
    {
        var state = new CountState();
        state.Path.Add(domainName.TrimEnd('.').ToLowerInvariant());
        var termCosts = new List<SpfTermCost>();
        foreach (var term in record.Terms.Where(term => term.CostsLookup))
        {
            var before = state.Total;
            await CountTermAsync(term, state, depth: 1, cancellationToken).ConfigureAwait(false);
            termCosts.Add(new SpfTermCost(term.Text, state.Total - before));
            if (state.Total > StopCountingAt)
            {
                break;
            }
        }

        if (state.VoidLookups > MaxVoidLookups)
        {
            state.Problems.Add($"{state.VoidLookups} lookups found nothing; receivers allow at most {MaxVoidLookups}.");
        }

        return new SpfLookupCount(state.Total, termCosts, state.MissingTargets, state.Problems);
    }

    private async Task CountTermAsync(SpfTerm term, CountState state, int depth, CancellationToken cancellationToken)
    {
        state.Total++;
        if (term.Target is not { } target || state.Total > StopCountingAt)
        {
            return;
        }

        if (target.Contains('%'))
        {
            state.Problems.Add($"{term.Text} uses a macro, so it wasn't followed.");
            return;
        }

        if (depth > MaxDepth)
        {
            state.Problems.Add($"{term.Text} is nested more than {MaxDepth} levels deep, so counting stopped there.");
            // Too deep to finish counting is as bad as too many: receivers give up too.
            state.Total = Math.Max(state.Total, SpfLookupCount.Limit + 1);
            return;
        }

        var key = target.TrimEnd('.').ToLowerInvariant();
        if (state.Path.Contains(key))
        {
            state.Problems.Add($"{term.Text} loops back to a domain it's already inside.");
            return;
        }

        var records = (await lookup.GetTxtValuesAsync(target, cancellationToken).ConfigureAwait(false)).Where(SpfRecord.IsSpf).ToList();
        if (records.Count == 0)
        {
            state.VoidLookups++;
            state.MissingTargets.Add(target);
            state.Problems.Add($"{target} has no SPF record.");
            return;
        }

        if (records.Count > 1)
        {
            state.Problems.Add($"{target} has {records.Count} SPF records, so receivers treat it as an error.");
        }

        // The path, not every domain seen so far: the same include in two branches is fine and costs twice.
        state.Path.Add(key);
        foreach (var inner in SpfRecord.Parse(records[0]).Terms.Where(innerTerm => innerTerm.CostsLookup))
        {
            await CountTermAsync(inner, state, depth + 1, cancellationToken).ConfigureAwait(false);
            if (state.Total > StopCountingAt)
            {
                break;
            }
        }

        state.Path.Remove(key);
    }

    private sealed class CountState
    {
        public int Total { get; set; }
        public int VoidLookups { get; set; }
        public HashSet<string> Path { get; } = [];
        public List<string> MissingTargets { get; } = [];
        public List<string> Problems { get; } = [];
    }
}
