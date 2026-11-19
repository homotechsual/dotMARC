using System.Globalization;

namespace DotMarc.Notifications;

/// <summary>One scope entry as the editor or a caller supplies it.</summary>
public sealed record AlertRuleScopeInput(AlertRuleScopeTarget Target, int TargetId, bool Exclude);

/// <summary>Everything about a rule that an administrator can set. <see cref="Parameters"/> holds the values for the kind's
/// parameters, as text; one the caller leaves out means the default.</summary>
public sealed record AlertRuleInput(
    string Name,
    bool Enabled,
    string Severity,
    bool StopProcessing,
    int? CooldownMinutes,
    int? AutoCloseDays,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<AlertRuleScopeInput> Scope);

/// <summary>Checks a rule against its kind's description. Every problem is a sentence that names the field, so a person can fix it
/// without guessing; all problems are returned, not just the first.</summary>
public static class AlertRuleValidator
{
    public const int MaxNameLength = 200;
    public const int MaxCooldownMinutes = 10080;
    public const int MaxAutoCloseDays = 365;

    public static IReadOnlyList<string> Problems(string kind, AlertRuleInput input)
    {
        var problems = new List<string>();
        if (AlertRuleKinds.Find(kind) is not { } definition)
        {
            problems.Add($"'{kind}' is not a kind of alert dotMARC raises.");
            return problems;
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            problems.Add("Name is required.");
        }
        else if (input.Name.Length > MaxNameLength)
        {
            problems.Add($"Name must be {MaxNameLength} characters or fewer.");
        }

        if (!AlertSeverities.All.Contains(input.Severity))
        {
            problems.Add($"Severity must be {string.Join(", ", AlertSeverities.All.Take(AlertSeverities.All.Count - 1))} or {AlertSeverities.All[^1]}.");
        }

        if (input.CooldownMinutes is < 0 or > MaxCooldownMinutes)
        {
            problems.Add($"Cooldown must be from 0 to {MaxCooldownMinutes} minutes.");
        }

        if (input.AutoCloseDays is { } autoCloseDays)
        {
            if (!definition.HasAutoClose)
            {
                problems.Add("Close after (days) doesn't apply to this kind of alert.");
            }
            else if (autoCloseDays is < 0 or > MaxAutoCloseDays)
            {
                problems.Add($"Close after (days) must be from 0 to {MaxAutoCloseDays}.");
            }
        }

        foreach (var (key, value) in input.Parameters)
        {
            var parameter = definition.Parameters.FirstOrDefault(candidate => candidate.Key == key);
            if (parameter is null)
            {
                problems.Add($"'{key}' is not a setting of this kind of alert.");
            }
            else if (ParameterProblem(parameter, value) is { } problem)
            {
                problems.Add(problem);
            }
        }

        problems.AddRange(ScopeProblems(definition, input.Scope));
        return problems;
    }

    private static string? ParameterProblem(AlertRuleParameterDefinition parameter, string value)
    {
        if (parameter.Type == AlertRuleParameterType.Integer)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= parameter.Min && number <= parameter.Max
                ? null
                : $"{parameter.Label} must be a whole number from {parameter.Min} to {parameter.Max}.";
        }

        return parameter.Choices!.Any(choice => choice.Value == value)
            ? null
            : $"{parameter.Label} must be one of: {string.Join(", ", parameter.Choices!.Select(choice => choice.Label))}.";
    }

    private static IEnumerable<string> ScopeProblems(AlertRuleKind definition, IReadOnlyList<AlertRuleScopeInput> scope)
    {
        if (scope.Count == 0)
        {
            yield break;
        }

        if (!definition.HasDomainScope)
        {
            yield return "This kind of alert is not about a domain, so it has no scope.";
            yield break;
        }

        if (scope.Any(entry => entry.TargetId <= 0))
        {
            yield return "A scope entry has no group, tag or domain selected.";
        }

        if (scope.GroupBy(entry => (entry.Target, entry.TargetId, entry.Exclude)).Any(group => group.Count() > 1))
        {
            yield return "The same scope entry is listed twice.";
        }
    }
}
