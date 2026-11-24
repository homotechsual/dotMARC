using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Notifications;

public enum DnsHealthActionKind
{
    Raise,
    Resolve
}

/// <summary>What the evaluator wants done: raise an alert for a rule (through AlertingService.EnsureAlertAsync, which
/// de-duplicates and applies the cooldown) or resolve one. A resolve with no rule closes the alert type's alerts for every
/// rule; with a rule, only that rule's.</summary>
public sealed record DnsHealthAlertAction(DnsHealthActionKind Kind, string AlertType, ResolvedAlertRule? Rule = null, string Title = "", string Message = "")
{
    public string Severity => Rule?.Severity ?? "";
    public int? RuleId => Rule?.RuleId;

    public static DnsHealthAlertAction Resolve(string alertType, ResolvedAlertRule? rule = null) => new(DnsHealthActionKind.Resolve, alertType, rule);
}

/// <summary>Decides, for one monitored domain, which DNS health alerts to raise or resolve, from its stored check
/// results and the state remembered from earlier cycles. No database or clock of its own: it updates the states it's
/// given (adding any that are missing) and returns the actions. See
/// docs/superpowers/specs/2026-10-02-dns-health-alerts-design.md.</summary>
public static class DnsHealthAlertEvaluator
{
    /// <summary>How long after a first failure the confirmation recheck becomes due.</summary>
    public static readonly TimeSpan ConfirmationDelay = TimeSpan.FromMinutes(15);

    /// <summary>The rules decide which alerts may be raised for the domain and how: a check is quiet when no rule applies to it,
    /// and each applicable rule's mode decides whether it alerts.</summary>
    public static IReadOnlyList<DnsHealthAlertAction> Evaluate(Domain domain, List<DomainAlertState> states, AlertRuleSet rules, DateTimeOffset nowUtc, EffectiveDmarcTarget? dmarcTarget = null)
    {
        var facts = new DomainFacts(domain.Id, domain.Groups.Select(group => group.Id).ToHashSet(), domain.Tags.Select(tag => tag.Id).ToHashSet());
        var actions = new List<DnsHealthAlertAction>();
        foreach (var check in DnsHealthChecks.All)
        {
            EvaluateCheck(check, domain, StateFor(states, domain, check.Item), rules.For(check.AlertType, facts), nowUtc, actions);
        }

        EvaluatePolicy(domain, StateFor(states, domain, DnsHealthItems.DmarcPolicy), rules.For(AlertTypes.DmarcPolicyWeakened, facts), nowUtc, actions);
        EvaluateTarget(domain, StateFor(states, domain, DnsHealthItems.DmarcTarget), rules.For(AlertTypes.DmarcPolicyBelowTarget, facts), dmarcTarget, nowUtc, actions);
        EvaluateNameservers(domain, StateFor(states, domain, DnsHealthItems.Nameservers), rules.For(AlertTypes.NameserversChanged, facts), nowUtc, actions);
        return actions;
    }

    /// <summary>A set of nameservers in one comparable form: lower case, no trailing dot, sorted, ";"-joined. Null
    /// when there are none.</summary>
    public static string? NameserverKey(IEnumerable<string> nameservers)
    {
        var names = nameservers
            .Select(nameserver => nameserver.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(nameserver => nameserver.Length > 0)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        return names.Count == 0 ? null : string.Join(';', names);
    }

    private static void EvaluateCheck(DnsHealthCheck check, Domain domain, DomainAlertState state, IReadOnlyList<ResolvedAlertRule> applicable, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        switch (check.Health(domain))
        {
            case DnsCheckHealth.Passing:
                state.HasPassed = true;
                ClearPending(state);
                actions.Add(DnsHealthAlertAction.Resolve(check.AlertType));
                return;
            case DnsCheckHealth.Ignored:
                ClearPending(state);
                actions.Add(DnsHealthAlertAction.Resolve(check.AlertType));
                return;
        }

        // A rule wants an alert for a failing check by its mode: always, or only once the check has passed at least once so a
        // domain that was never set up stays quiet. A rule that does not set a mode uses the global setting for the check.
        var wanting = applicable.Where(rule => WantsAlert(rule.Mode(), state)).ToList();
        if (wanting.Count == 0)
        {
            // No rule applies, or none wants this failure (never passed, or switched off): clear the pending failure and close
            // any alerts left from when a rule did.
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(check.AlertType));
            return;
        }

        if (IsConfirmed(state, check.CheckedUtc(domain), nowUtc))
        {
            var detail = check.Detail(domain) is { Length: > 0 } text ? $" ({text})" : "";
            var history = state.HasPassed ? " It was passing before." : "";
            var message = $"The {check.Label} check for {domain.Name} is failing: {Words(check.Status(domain))}{detail}.{history}";
            foreach (var rule in applicable)
            {
                actions.Add(wanting.Contains(rule) ? Raise(check.AlertType, rule, message) : DnsHealthAlertAction.Resolve(check.AlertType, rule));
            }
        }
    }

    private static bool WantsAlert(DnsHealthAlertMode mode, DomainAlertState state) =>
        mode == DnsHealthAlertMode.WheneverItFails || (mode == DnsHealthAlertMode.WhenItBreaks && state.HasPassed);

    private static void EvaluatePolicy(Domain domain, DomainAlertState state, IReadOnlyList<ResolvedAlertRule> applicable, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        if (applicable.Count == 0)
        {
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.DmarcPolicyWeakened));
            return;
        }

        if (DmarcPolicyTags.Of(domain) is not { } current)
        {
            // No record: that's the DMARC check's alert. Leave this one as it is until the record is back.
            ClearPending(state);
            return;
        }

        if (DmarcPolicyTags.Parse(state.Baseline) is not { } baseline)
        {
            state.Baseline = current.Format();
            ClearPending(state);
            return;
        }

        if (!current.IsWeakerThan(baseline))
        {
            state.Baseline = current.Format();
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.DmarcPolicyWeakened));
            return;
        }

        if (IsConfirmed(state, domain.DmarcCheckedUtc, nowUtc))
        {
            var message = $"The DMARC policy for {domain.Name} went from {baseline.Format()} to {current.Format()}.";
            actions.AddRange(applicable.Select(rule => Raise(AlertTypes.DmarcPolicyWeakened, rule, message)));
        }
    }

    private static void EvaluateTarget(Domain domain, DomainAlertState state, IReadOnlyList<ResolvedAlertRule> applicable, EffectiveDmarcTarget? target, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        var outcome = applicable.Count == 0 ? DmarcTargetOutcome.NoTarget : DmarcTargetCheck.Outcome(domain, target);
        switch (outcome)
        {
            case DmarcTargetOutcome.NoTarget:
            case DmarcTargetOutcome.Meets:
                ClearPending(state);
                actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.DmarcPolicyBelowTarget));
                return;
            case DmarcTargetOutcome.NeedsChecking:
                ClearPending(state);
                return;
        }

        if (DmarcPolicyTags.Of(domain) is not { } observed)
        {
            // No record: the DMARC record broken alert covers it. Leave this one as it is until the record is back.
            ClearPending(state);
            return;
        }

        if (IsConfirmed(state, domain.DmarcCheckedUtc, nowUtc))
        {
            var message = $"The DMARC policy for {domain.Name} is {DmarcTargetText.Observed(observed)}; its target is {DmarcTargetText.Policy(target!)} ({DmarcTargetText.Source(target!)}). Reasons: {string.Join("; ", DmarcTargetCheck.Reasons(domain, target).Select(reason => reason.TrimEnd('.')))}.";
            actions.AddRange(applicable.Select(rule => Raise(AlertTypes.DmarcPolicyBelowTarget, rule, message)));
        }
    }

    private static void EvaluateNameservers(Domain domain, DomainAlertState state, IReadOnlyList<ResolvedAlertRule> applicable, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        if (applicable.Count == 0)
        {
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.NameserversChanged));
            return;
        }

        if (NameserverKey(domain.DnsNameservers) is not { } current)
        {
            ClearPending(state);
            return;
        }

        if (state.Baseline is null)
        {
            state.Baseline = current;
            ClearPending(state);
            return;
        }

        if (current == state.Baseline)
        {
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.NameserversChanged));
            return;
        }

        if (IsConfirmed(state, domain.DnsProviderCheckedUtc, nowUtc))
        {
            var message = $"The nameservers for {domain.Name} changed from {state.Baseline.Replace(";", ", ")} to {current.Replace(";", ", ")}. The DNS provider is now {domain.DnsProvider}.";
            actions.AddRange(applicable.Select(rule => Raise(AlertTypes.NameserversChanged, rule, message)));
        }
    }

    /// <summary>Starts a pending failure if there isn't one. True once the check has run again at or after the
    /// recheck time, so a one-off DNS blip never alerts.</summary>
    private static bool IsConfirmed(DomainAlertState state, DateTimeOffset? checkedUtc, DateTimeOffset nowUtc)
    {
        if (state.PendingSinceUtc is null)
        {
            state.PendingSinceUtc = nowUtc;
            state.RecheckDueUtc = nowUtc + ConfirmationDelay;
            return false;
        }

        return checkedUtc is { } lastChecked && state.RecheckDueUtc is { } due && lastChecked >= due;
    }

    private static void ClearPending(DomainAlertState state)
    {
        state.PendingSinceUtc = null;
        state.RecheckDueUtc = null;
    }

    private static DomainAlertState StateFor(List<DomainAlertState> states, Domain domain, string item)
    {
        var state = states.FirstOrDefault(candidate => candidate.Item == item);
        if (state is null)
        {
            state = new DomainAlertState { DomainId = domain.Id, Item = item };
            states.Add(state);
        }

        return state;
    }

    private static DnsHealthAlertAction Raise(string alertType, ResolvedAlertRule rule, string message) =>
        new(DnsHealthActionKind.Raise, alertType, rule, AlertTypes.Find(alertType)!.DisplayName, message);

    /// <summary>"MissingRecord" as "missing record".</summary>
    private static string Words(string status) =>
        string.Concat(status.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? " " + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
