# Quiet hours, escalation and rule read endpoints (alert rules, Phase 4a)

Follows [2026-11-19-alert-destinations-design.md](2026-11-19-alert-destinations-design.md). Phase 4 of the [alert rules design](2026-11-19-alert-rules-design.md) named three things: quiet hours and escalation, a metric threshold kind, and rule read endpoints. The metric kind is a separate project and is not covered here (it changes the detection engine and needs its own design). This document covers the other two.

## Intent

No two MSPs work the same way. Some do not want a page at 3am for an Info alert, some want a second channel told when nobody has picked an alert up, and some want to read their rules from a script. All three are per-rule choices, in the same way destinations and ticket mode are.

## Goals

- A rule can hold or mute its notifications during a daily quiet window.
- A rule can notify extra destinations when its alert is still unacknowledged after a time.
- Rules and destination names can be read through the API.
- Existing installs behave exactly as before until someone edits a rule.

## Non-goals

- The metric threshold kind (its own project).
- Per-weekday schedules, holidays, on-call rotations, repeated escalation levels.
- Quiet hours for tickets. A quiet window affects notifications only; alerts are still raised and recorded, and tickets still open.
- Writing rules or destinations through the API.

## Approaches considered

1. **Track delivery on the alert (chosen).** Each alert records when it was notified and when it was escalated. The existing monitor cycle sends what is due. Survives restarts, no new table. Timing is as precise as the monitor interval (5 minutes by default).
2. A table of pending deliveries (alert, destination, due, sent). More general and allows retries, but much more to build and reason about.
3. In-memory timers. Lost on restart. Ruled out.

## Design

### Data

- `AlertRule.QuietMode`: `Off` (default), `Hold`, `Mute`. `QuietStart` and `QuietEnd` as local times of day (minutes after midnight, 0 to 1439); a window with start after end crosses midnight; start equal to end is refused. `QuietIgnoredForCritical` (default true). `EscalateAfterMinutes` (null means no escalation, otherwise 5 to 10080). `AlertRuleEscalationDestination` join table (rule, destination) with cascade deletes, same shape as `AlertRuleDestination`.
- `NotificationSettings.TimeZoneId` (IANA id, default `UTC`), validated against the platform's time zone database.
- `AlertEvent.NotifiedUtc` and `AlertEvent.EscalatedUtc` (nullable). The migration sets `NotifiedUtc = CreatedUtc` on every existing alert so nothing old is sent again.

### Delivery

- When an alert is raised, the rule's quiet window is checked at that moment in the configured time zone.
  - Not in a quiet window, or the alert is Critical and the rule ignores quiet hours for Critical: notify now and set `NotifiedUtc`.
  - In a window, mode `Hold`: record the alert and leave `NotifiedUtc` empty. The monitor cycle sends it once the window has ended, if it is still open, then sets `NotifiedUtc`. An alert resolved or acknowledged in the meantime is never sent.
  - In a window, mode `Mute`: record the alert, set `NotifiedUtc` to now, send nothing.
- Escalation: the cycle finds open, unacknowledged alerts whose rule has `EscalateAfterMinutes`, that were notified (or muted, so escalation still works for a muted alert) at least that long ago and have no `EscalatedUtc`. It sends to the rule's escalation destinations (same routing as normal sending: enabled, with a URL), prefixed in the title with "Escalated:", then sets `EscalatedUtc`. A rule with no escalation destinations ticked escalates to nobody and does nothing.
- Escalation ignores quiet hours (that is the point of it) but never fires for an alert still being held.
- "Unacknowledged" means open and, for kinds that can be acknowledged, not acknowledged. Kinds that cannot be acknowledged escalate while they stay open.

### UI

- Rule dialog: a Quiet hours section (mode, start, end, Critical switch) and an Escalation section (minutes, destination checklist). Rules page row summary adds "Quiet 22:00 to 07:00 (held)" and "Escalates after 30 minutes".
- Alert settings: a Time zone select.
- Alerts page: an alert that is held shows "Notification held until the quiet window ends" in its detail; a muted one shows "Muted by quiet hours".

### API

- `GET /alert-rules` (`AlertsView`, key with no group limit; filters `kind`, `enabled`; paged like the other lists): id, kind, name, enabled, severity, position, stopProcessing, cooldownMinutes, autoCloseDays, parameters, destinationMode, destinations (names and ids), ticketMode, quiet hours, escalation, and the scope described in words plus ids.
- `GET /alert-rules/{id}`.
- `GET /alert-destinations`: id, name, kind, enabled. Never the URL.
- A key limited to groups gets a 404 on the rule endpoints and an empty list on the destination endpoint, consistent with how the rules page is closed to them.

### Permissions and audit

Choosing quiet hours and escalation needs `AlertRulesManage`. The time zone needs `AlertsManage` and is audited. Rule audit includes the new fields.

### Left to the builder

Column types, the time picker's look, validation wording, the escalation title prefix wording, how the Alerts page shows delivery state.

## Anti-goals

An alert silently never sent: a held alert that is lost, or an existing install whose old alerts are sent again after the upgrade. An escalation that fires repeatedly.
