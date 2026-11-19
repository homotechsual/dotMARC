# Alert rules

Turns the fixed alerting into rules an administrator can add, change and scope. Today's behaviour becomes the default pack of rules, so an upgrade changes nothing until someone edits a rule.

## Goal

Let an administrator decide, per rule, **what raises an alert, for which domains, how serious it is, and what happens when it fires**. Every alert dotMARC raises today becomes a rule kind, and every threshold, switch and mode on the Alert settings page becomes a parameter of a rule. An administrator can add several rules of the same kind with different settings for different domains, groups or tags.

The first thing this unlocks is a setting for **Under review**. Today a sender marked Under review counts as decided, so it silences the new-sender alert and the Overview count forever. A rule can instead keep raising the alert, or raise it again after a number of days.

## Why

No two MSPs work the same way, and dotMARC is also used for internal IT. One MSP wants a missing-report alert after two days for everyone, another wants five days for one client that reports weekly. An internal team wants no PSA tickets at all. Today every threshold is one global number on a single settings row, severity is fixed per alert type, and everything goes to the same channels. Granularity has to reach every setting, with defaults that keep working for the person who never opens the editor.

## Goals

- A rule is a built-in **kind** plus **parameters**, a **scope**, a **severity** and **delivery** settings, and an enabled switch.
- Any number of rules per kind. For one domain and kind, exactly one rule applies: the most specific scope wins.
- A **default pack**: one rule per kind, scoped to all domains, with parameters equal to today's behaviour. On an existing install the pack is seeded from the values currently on the settings row, so no customisation is lost.
- A new permission, **Manage alert rules**, that gates creating, editing, ordering, disabling and deleting rules. Viewing rules needs the existing alert view permission.
- Every rule change is recorded in the audit log, with what changed.
- The editor is generated from a description of each kind's parameters in code, so a new kind or parameter does not need new screens.
- Alerts say which rule raised them.

## Non-goals

- No condition builder over arbitrary data (a rule cannot be "pass rate below 90% for three days" in this version). The detections stay code. See Approaches.
- No new kinds of detection in the first three phases.
- No change to what an alert says or to the alert's lifecycle (raise, cooldown, acknowledge, resolve).
- No change to the PSA ticket rule screens. They keep deciding ticket creation per group and alert type. A rule can override them (see Delivery).
- No editing rules through the public API. Rules are readable through it later, if wanted.

## Anti-goals

- A default install must behave as it does today, including the confirmation delay on DNS failures, first-run baselining and the cooldowns.
- An admin must not be able to save a rule that makes the app crash or flood: every parameter has a range, and a rule that fails validation is refused with a message that names the parameter.
- The editor must not feel like writing code. A person who opens it should be able to change one number and save.

## Approaches

**A. Rule kinds with full parameters (chosen).** Each rule is an instance of a built-in detection. The detection logic stays in code, tested as it is now, and takes its settings from the rule instead of the global row. Granular in every setting, scope and delivery choice. A genuinely new detection still needs a release.

**B. A condition builder.** Rules are free conditions over facts about a domain, replacing the detectors. The most granular, but a much larger project, and behaviour such as the 15 minute DNS confirmation and first-run baselining does not fit a generic engine. Rebuilding today's alerts on it risks changing what people rely on.

**C. A now, a metric rule later.** A's model gets a generic "metric threshold" kind later. This is the path from A towards B without discarding tested detectors. A is built so it fits.

## What a rule is

```
New sender to review            on    Warning
Applies to    Group "Aurora Retail", Tag "Finance"     except shop.aurora-retail.example
Raise when    first seen within 7 days, at least 20 messages
Under review  counts as decided for 14 days, then alert again
Delivery      cooldown 3 hours, tickets follow the ticket rules, send to Teams
```

A rule has:

- **Kind**: which detection it configures. Fixed once the rule is created.
- **Name**: shown in lists and on the alerts it raises. The default pack's names are the kinds' display names.
- **Enabled**, **severity** (Info, Warning, Critical), **priority** (order, for ties).
- **Scope**: included groups, tags and domains, and excluded groups, tags and domains. Empty includes mean all domains. A kind with no domain, such as API key expiry, has no scope.
- **Parameters**: a set of named values defined by the kind (below).
- **Delivery**: cooldown in minutes (blank uses the global cooldown), auto-close after N days where the kind supports it, ticket mode (follow the ticket rules, never, or always), and destinations (Phase 3).

### Kinds and their parameters

| Kind | Parameters (defaults are today's values) |
| --- | --- |
| Missing DMARC report | days without a report (2) |
| Suspicious reject activity | minimum messages (10), non-benign percent (50) |
| TLS delivery failures | none |
| Mail on a null-routed domain | none |
| DMARC, DMARC authorization, TLS-RPT, SPF, MX, DKIM, MTA-STS record broken | mode: when it breaks, or whenever it fails (when it breaks). Off is the enabled switch. |
| DMARC policy weakened, Nameservers changed | auto-close after days (0, never) |
| New sender to review | days a sender counts as new (7), minimum messages (20), **Under review handling** |
| Unauthorised sender mail delivered | minimum messages (10) |
| API key expiring, Client report failed, PSA connection lost | none. These belong to the system, not a domain, so they have no scope. |

**Under review handling** is a parameter of New sender to review: *counts as decided* (today's behaviour and the default), *counts as unreviewed*, or *counts as decided for N days, then alert again* (N from 1 to 365, measured from when the decision was made). Only the alert follows this setting. The Overview "senders need a decision" count and the decision column are unchanged.

## Which rule applies

For one domain and kind, the applicable rule is the enabled rule whose scope matches the domain, with the most specific scope winning: a rule naming the domain, then one matching one of its tags, then one matching one of its groups, then an all-domains rule. Exclusions remove a domain from a rule's reach before this ranking. Ties at the same specificity go to the lower priority number. If no enabled rule applies, no alert of that kind is raised for the domain, and an open one is resolved.

Because one rule applies per domain and kind, an alert is still unique per domain and type, so cooldown, acknowledgement and tickets keep working unchanged. Alerts gain a nullable link to the rule that raised them.

Default pack rules cannot be deleted. They can be disabled, edited, scoped and reset to the shipped values. Disabling the all-domains default and relying on a narrower rule is allowed and means other domains are not alerted.

## The editor

A new **Alert rules** page under Manage, shown to people with Manage alert rules. A list grouped by kind, with the scope summarised in words, the severity, whether it is on, and how many domains it applies to. Opening a rule edits it in a dialog generated from the kind's parameter definitions: each parameter has a label, a help sentence, a type (number, choice, switch), a range and a default. **Reset to default** restores the shipped values. **Add a rule** picks a kind and starts from its default values.

The existing Alert settings page keeps what is not per rule: the master switch, the channels and their webhook URLs, the global cooldown and the monitor interval. The threshold and mode fields it has now move to the rules, with a note on the page saying where they went.

A rule editor shows "Applies to N domains" live as the scope changes, and a warning when another rule of the same kind and the same specificity also matches some of those domains and wins.

## Migration

One migration adds the rule tables, then seeds the default pack from the current settings row (for example a custom missing-report threshold of five days becomes the default rule's five). The old settings columns stop being read and stay in place for one release, marked obsolete, then are dropped. This keeps a downgrade possible for a release. Seeding is keyed by a stable key per default rule, so running it again never duplicates, and a future kind or parameter added to the pack is added to existing installs without touching rules the admin changed.

## Permissions and audit

A new permission, `AlertRulesManage`, added to the permission set with its place on the Access page. The Admin role has it. Existing custom roles do not, so on upgrade nobody who could edit thresholds yesterday loses a view of them, but they can no longer change them until granted. The release notes say so. Rule changes are audited with the rule, the field and the old and new values, using the existing audit log.

## Phases

1. **Engine and default pack.** Rule tables, the kinds catalogue with parameter definitions, scope resolution, the detections reading rule parameters, the migration and seeding, alerts linked to their rule. No new screen. Behaviour is identical to today: the existing alerting tests pass against rules built from defaults, plus tests for scope resolution and for migration of custom values.
2. **Editor, permission, audit, Under review.** The Alert rules page, the new permission, audit entries, the Under review parameter, the changed Alert settings page, demo data (a scoped rule and an Under review example), and the docs.
3. **Destinations.** Named webhook destinations (a type and a URL, any number) replacing the three fixed channels, chosen per rule. The existing channels migrate to destinations. Ticket mode per rule.
4. **Later, named only.** Quiet hours and escalation, a metric threshold kind, rule read endpoints in the API.

## Open points settled here (my calls)

- Most specific scope wins and rules never stack, so overlapping rules do not double-alert. If someone needs two different outcomes for one condition, that is one rule with several destinations (Phase 3).
- Severity gets a Critical level in addition to today's Warning and Info. Existing alerts keep theirs.
- Only the alert follows the Under review handling. The Overview count does not.
- Old settings columns are kept for one release rather than dropped in the migration.
- Rule names are free text, unique per kind, and not a stable key. The default pack uses a separate hidden key.

## Left to the builder

Wherever severity is displayed or mapped (webhook colours, PSA priority, the Alerts page and API), how Critical is shown. The table layout and column types, the shape of the stored parameters, how the scope tester explains a match, the wording of each parameter's help text, and how the parameter descriptions are expressed in code.
