# Alert destinations and ticket mode (alert rules, Phase 3)

Follows [2026-11-19-alert-rules-design.md](2026-11-19-alert-rules-design.md), which names this as Phase 3.

## Intent

No two MSPs work the same way. Today every alert goes to three fixed channels (Teams, Slack, a generic webhook), one URL each. An MSP wants one client's alerts in that client's Teams channel and the rest in a shared one, and wants some rules to open tickets regardless of the ticket rules, or never. This phase makes both a per-rule choice.

## Goals

- Any number of named destinations, each with a kind (Teams, Slack, generic), a URL and an on/off switch.
- Each rule chooses where its alerts go.
- Each rule chooses its ticket mode.
- Existing installs behave exactly as before until someone edits a rule.

## Non-goals

- Routing by group on the destination itself. Rules already scope.
- New destination kinds (email, PagerDuty). The kind list stays Teams, Slack, generic.
- Changing payload formats.
- Quiet hours, escalation, API read endpoints (Phase 4).

## Approaches considered

1. **Destinations table plus a rule join table (chosen).** Foreign keys, so deleting a destination cleans up and "which rules use this" is answerable.
2. Destination ids in the rule's JSON parameters. Less work, but dangling ids and no usage lookup.
3. Group routing on the destination. Duplicates rule scoping.

## Design

### Data

- `AlertDestination`: Id, Name (unique, case-insensitive), Kind (`Teams`, `Slack`, `Generic`), Url, Enabled, CreatedUtc.
- `AlertRule.DestinationMode`: `All` (default) or `Selected`.
- `AlertRuleDestination` join: RuleId, DestinationId, cascade delete on both sides.
- `AlertRule.TicketMode`: `FollowRules` (default), `Never`, `Always`.
- Migration creates the tables and one destination per channel that has a URL (name "Teams", "Slack", "Generic webhook", the existing switch becomes Enabled). The old `NotificationSettings` channel columns stay for one release and are no longer read. A channel with no URL creates no destination. Seeding is done by the migration, not lazily, so it cannot run twice.

### Delivery

- Sending takes the alert's rule: `All` sends to every enabled destination with a URL; `Selected` sends to the selected ones that are enabled (an empty list sends nowhere). The master switch still stops everything. Failures stay isolated per destination and are logged.
- Alerts with no rule (system alerts that predate rules, or a deleted rule) use `All`.
- Send test works per destination, saved or not.

### Ticket mode

`AlertTicketPolicy` gains a rule-mode input: `Never` returns false, `Always` returns true, `FollowRules` is today's behaviour. `Always` still needs a ready PSA and a linked company, since there is nowhere to put the ticket otherwise. The alert's rule is read from `AlertRuleId`.

### UI

- Alert settings: the three channel cards become a Destinations list (add, edit, delete, test, switch). Deleting says how many rules select it.
- Rule dialog: a Destinations section (All, or pick from the list, with a note that none selected means quiet) and a Ticket mode choice. Ticket mode is hidden for alert kinds that never create tickets today (the ones the ticket rule screens already leave out).
- Rule list shows destinations and ticket mode in the row summary when they differ from the defaults.

### Permissions and audit

- Managing destinations: `AlertsManage`. Choosing them and ticket mode on a rule: `AlertRulesManage`.
- Audited like secrets for the URL (changed or not, never the value). Rule audit includes the new fields. New audit actions `alert_destination.created/updated/deleted`.
- Viewing: destination names show on the rules page to people who can already view rules. URLs show only to `AlertsManage`.

### Left to the builder

Table and column layout, validation wording, the destination picker's look, how the demo seeds destinations (the demo has no real URLs, so it seeds none and shows All).

## Anti-goals

Alerts silently going nowhere after the upgrade. A migrated install must send to the same places as before.
