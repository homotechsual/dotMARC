# Metric alert kinds (alert rules, Phase 4b)

Follows [2026-11-19-quiet-hours-escalation-design.md](2026-11-19-quiet-hours-escalation-design.md). Phase 4 of the [alert rules design](2026-11-19-alert-rules-design.md) named a "metric threshold kind" as approach C: a way to alert on a number crossing a value without a code change. This document builds it as three small kinds rather than one generic one.

## Intent

No two MSPs work the same way. Some want to hear when a client's DMARC pass rate drops, some when mail volume falls off a cliff, some when a flood of mail is being rejected. Each is a number the reports already carry, so an administrator should be able to turn on, tune and scope an alert for it from the Alert rules page, with no release needed.

## Goals

- Three new kinds of alert, each with a few settings, raised for monitored domains from the DMARC reports already received.
- They reuse everything rules already have: scope, severity, cooldown, destinations, ticket mode, quiet hours, escalation, audit, API.
- Existing installs behave exactly as before until someone switches one on.

## Non-goals

- A free-form condition or expression builder (the spec's approach B, rejected there).
- Metrics other than these three, per-sender or per-source metrics, quarantine-only or reject-only counts.
- Alerting on domains that are not monitored (as every other detector).
- Statistical anomaly detection or smoothing.

## Approaches considered

1. One generic "Metric threshold" kind where the rule chooses the metric. Flexible, but every metric raises the same alert type, so the Alerts filters, the API filters and the per-type ticket rules cannot tell them apart, and a kind with no default rule breaks the "one default rule per kind" model.
2. **Three kinds, one per metric (chosen).** Each has its own alert type, so filtering and ticket rules work unchanged, and each fits the default-pack model.
3. A condition builder. Too large and risky for what is wanted.

## Design

### The kinds

| Kind (alert type key) | Display name | Raised when |
| --- | --- | --- |
| `PassRateLow` | DMARC pass rate low | The domain's pass rate over the last N days is below a percentage, and it received at least a minimum number of messages |
| `VolumeChanged` | Message volume changed | Message volume over the last N days has dropped (or risen, or either) by at least a percentage compared with the N days before, and the earlier period had at least a minimum number of messages |
| `RejectedMessagesHigh` | Rejected or quarantined messages high | The domain had at least a number of messages with a reject or quarantine disposition over the last N days |

Parameters (each with a label, help text and a range, validated with a message naming it):

- `PassRateLow`: `belowPercent` 1 to 99 (default 90), `windowDays` 1 to 30 (default 7), `minimumMessages` 1 to 1000000 (default 100).
- `VolumeChanged`: `direction` choice drop, rise or either (default drop), `changePercent` 10 to 500 (default 50), `windowDays` 1 to 15 (default 7; two windows must fit the 30 days of reports kept in the cycle), `minimumMessages` 1 to 1000000 (default 100, applied to the earlier period).
- `RejectedMessagesHigh`: `messagesAtLeast` 1 to 1000000 (default 50), `windowDays` 1 to 30 (default 7).

Default severity `Warning` for all three. None creates tickets by default (`CreatesTicketByDefault` false).

### Detection

Pure evaluator functions (new `MetricAlertEvaluator`), tested like the existing detectors, taking the domain's reports with their records and the rule's parameters and returning whether to raise plus the numbers for the message. Pass counts use the existing `DomainStatistics.IsPassing`; reject and quarantine count messages (`MessageCount`) whose disposition is `Reject` or `Quarantine`. Windows are measured back from now by each report's received time.

The cycle runs them for each monitored domain through the existing rule machinery, so ordering, Stop processing, scope, per-rule cooldown and the sweep that closes a rule's alerts when it stops applying all work as for other kinds. An alert closes by itself when the number recovers. Message example: "contoso.io: DMARC pass rate was 71% over the last 7 days (threshold 90%, 420 messages)."

### Default pack

Each kind ships one default rule, applying to every domain, **switched off**. `AlertRuleKind` gains `EnabledByDefault` (true for the existing kinds, false for these three); seeding and "Reset to default values" use it. Existing installs get the three disabled rules on the next seeding, with no change in behaviour.

### Surfaces

The three kinds appear on the Alert rules page, the Alerts page type filter, the global ticket rules list and the API's `type` filters and `kind` filters, from the existing lists. The OpenAPI copy is regenerated. Docs gain a section on metric alerts. The demo leaves them off.

### Left to the builder

Help text wording, how the message rounds percentages, how the evaluator receives records (a projection rather than loaded entities if cheaper), column widths.

## Anti-goals

An upgrade that starts raising alerts nobody asked for. A rule that fires on a handful of messages. A volume rule that flaps every cycle at the boundary (the cooldown and the minimum messages are the guard; nothing more is added).
