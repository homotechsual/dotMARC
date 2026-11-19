using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>What decides which rules apply to a domain: its id, its groups and its tags.</summary>
public sealed record DomainFacts(int DomainId, IReadOnlySet<int> GroupIds, IReadOnlySet<int> TagIds)
{
    /// <summary>For a domain the caller could not find: only an all-domains rule matches it.</summary>
    public static DomainFacts None { get; } = new(0, new HashSet<int>(), new HashSet<int>());
}

/// <summary>A rule that applies to a domain, with its parameters read once.</summary>
public sealed record ResolvedAlertRule(AlertRule Rule, AlertRuleParameters Parameters)
{
    public int RuleId => Rule.Id;
    public string Severity => Rule.Severity;
}

/// <summary>The rules loaded for one cycle (or one event), and the logic for which of them apply to a domain.</summary>
public sealed class AlertRuleSet
{
    private readonly Dictionary<string, List<AlertRule>> _rulesByKind;
    private readonly Dictionary<AlertRule, AlertRuleParameters> _parameters;
    private readonly Dictionary<int, AlertRule> _rulesById;
    private readonly NotificationSettings _settings;

    public AlertRuleSet(IReadOnlyList<AlertRule> rules, NotificationSettings settings)
    {
        _settings = settings;
        _rulesByKind = rules
            .GroupBy(rule => rule.Kind)
            .ToDictionary(group => group.Key, group => group.OrderBy(rule => rule.Position).ThenBy(rule => rule.Id).ToList());
        _parameters = rules.ToDictionary(rule => rule, rule => new AlertRuleParameters(rule.ParametersJson));
        _rulesById = rules.Where(rule => rule.Id != 0).GroupBy(rule => rule.Id).ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>Makes sure the default pack exists, then loads every rule with its scope.</summary>
    public static async Task<AlertRuleSet> LoadAsync(DotMarcDbContext context, NotificationSettings settings, CancellationToken cancellationToken)
    {
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, cancellationToken).ConfigureAwait(false);
        var rules = await context.AlertRules.AsNoTracking().Include(rule => rule.Scope).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new AlertRuleSet(rules, settings);
    }

    /// <summary>The default pack in memory, with ids 1 and up by position. For tests and callers with no database.</summary>
    public static AlertRuleSet Defaults(NotificationSettings settings)
    {
        var pack = AlertRuleSeeder.DefaultPack();
        for (var index = 0; index < pack.Count; index++)
        {
            pack[index].Id = index + 1;
        }

        return new AlertRuleSet(pack, settings);
    }

    public AlertRule? Find(int ruleId) => _rulesById.GetValueOrDefault(ruleId);

    /// <summary>The rules of <paramref name="kind"/> that apply to a domain, in order, up to and including the first that stops
    /// processing. <paramref name="facts"/> is null for a kind that is not about a domain.</summary>
    public IReadOnlyList<ResolvedAlertRule> For(string kind, DomainFacts? facts)
    {
        if (!_rulesByKind.TryGetValue(kind, out var rules) || !AlertRuleKinds.IsEnabledBySettings(kind, _settings))
        {
            return [];
        }

        var hasDomainScope = AlertRuleKinds.Find(kind)?.HasDomainScope ?? true;
        var domain = facts ?? DomainFacts.None;
        var applicable = new List<ResolvedAlertRule>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || (hasDomainScope && !Matches(rule, domain)))
            {
                continue;
            }

            applicable.Add(new ResolvedAlertRule(rule, _parameters[rule]));
            if (rule.StopProcessing)
            {
                break;
            }
        }

        return applicable;
    }

    private static bool Matches(AlertRule rule, DomainFacts domain)
    {
        var includes = rule.Scope.Where(entry => !entry.Exclude).ToList();
        if (rule.Scope.Any(entry => entry.Exclude && Covers(entry, domain)))
        {
            return false;
        }

        return includes.Count == 0 || includes.Any(entry => Covers(entry, domain));
    }

    private static bool Covers(AlertRuleScopeEntry entry, DomainFacts domain) => entry.Target switch
    {
        AlertRuleScopeTarget.Group => domain.GroupIds.Contains(entry.TargetId),
        AlertRuleScopeTarget.Tag => domain.TagIds.Contains(entry.TargetId),
        AlertRuleScopeTarget.Domain => domain.DomainId == entry.TargetId,
        _ => false
    };
}
