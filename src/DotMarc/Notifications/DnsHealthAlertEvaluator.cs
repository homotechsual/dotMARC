using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Notifications;

public enum DnsHealthActionKind
{
    Raise,
    Resolve
}

/// <summary>What the evaluator wants done: raise an alert (through AlertingService.EnsureAlertAsync, which
/// de-duplicates and applies the cooldown) or resolve one.</summary>
public sealed record DnsHealthAlertAction(DnsHealthActionKind Kind, string AlertType, string Severity = "", string Title = "", string Message = "")
{
    public static DnsHealthAlertAction Resolve(string alertType) => new(DnsHealthActionKind.Resolve, alertType);
}

/// <summary>Decides, for one monitored domain, which DNS health alerts to raise or resolve, from its stored check
/// results and the state remembered from earlier cycles. No database or clock of its own: it updates the states it's
/// given (adding any that are missing) and returns the actions. See
/// docs/superpowers/specs/2026-10-02-dns-health-alerts-design.md.</summary>
public static class DnsHealthAlertEvaluator
{
    /// <summary>How long after a first failure the confirmation recheck becomes due.</summary>
    public static readonly TimeSpan ConfirmationDelay = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<DnsHealthAlertAction> Evaluate(Domain domain, List<DomainAlertState> states, NotificationSettings settings, DateTimeOffset nowUtc)
    {
        var actions = new List<DnsHealthAlertAction>();
        foreach (var check in DnsHealthChecks.All)
        {
            EvaluateCheck(check, domain, StateFor(states, domain, check.Item), settings, nowUtc, actions);
        }

        EvaluatePolicy(domain, StateFor(states, domain, DnsHealthItems.DmarcPolicy), settings, nowUtc, actions);
        EvaluateNameservers(domain, StateFor(states, domain, DnsHealthItems.Nameservers), settings, nowUtc, actions);
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

    private static void EvaluateCheck(DnsHealthCheck check, Domain domain, DomainAlertState state, NotificationSettings settings, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        var mode = check.Mode(settings);
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

        if (mode == DnsHealthAlertMode.Off)
        {
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(check.AlertType));
            return;
        }

        if (mode == DnsHealthAlertMode.WhenItBreaks && !state.HasPassed)
        {
            // Never passed, so it hasn't broken: a domain that was never set up stays quiet. Resolving closes any
            // alerts left from when the check was set to Whenever it fails.
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(check.AlertType));
            return;
        }

        if (IsConfirmed(state, check.CheckedUtc(domain), nowUtc))
        {
            var detail = check.Detail(domain) is { Length: > 0 } text ? $" ({text})" : "";
            var history = state.HasPassed ? " It was passing before." : "";
            actions.Add(Raise(check.AlertType, "Warning",
                $"The {check.Label} check for {domain.Name} is failing: {Words(check.Status(domain))}{detail}.{history}"));
        }
    }

    private static void EvaluatePolicy(Domain domain, DomainAlertState state, NotificationSettings settings, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        if (!settings.DmarcPolicyWeakenedEnabled)
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
            actions.Add(Raise(AlertTypes.DmarcPolicyWeakened, "Warning",
                $"The DMARC policy for {domain.Name} went from {baseline.Format()} to {current.Format()}."));
        }
    }

    private static void EvaluateNameservers(Domain domain, DomainAlertState state, NotificationSettings settings, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        if (!settings.NameserversChangedEnabled)
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
            actions.Add(Raise(AlertTypes.NameserversChanged, "Info",
                $"The nameservers for {domain.Name} changed from {state.Baseline.Replace(";", ", ")} to {current.Replace(";", ", ")}. The DNS provider is now {domain.DnsProvider}."));
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

    private static DnsHealthAlertAction Raise(string alertType, string severity, string message) =>
        new(DnsHealthActionKind.Raise, alertType, severity, AlertTypes.Find(alertType)!.DisplayName, message);

    /// <summary>"MissingRecord" as "missing record".</summary>
    private static string Words(string status) =>
        string.Concat(status.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? " " + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
