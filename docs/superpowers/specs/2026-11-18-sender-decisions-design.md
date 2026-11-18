# Mark senders as authorised or unauthorised

Builds on `2026-11-18-sender-identification-design.md`, which shows which sending services a domain's reports point to.

## Goal

Let a person record a decision about a sender: **Authorised** (it should send as this domain), **Unauthorised** (it should not), or **Under review**. Decisions are made at three levels that inherit from each other: a **baseline** for everything, an override **per group**, and a decision on a single **domain**. Then use the decision:

- Stop telling someone to fix a sender they have already decided to leave blocked.
- Say plainly whether an unauthorised sender is actually being blocked.
- Raise an alert when a new sender appears that nobody has looked at, or when unauthorised mail is getting through.
- Let the client report say how much unauthorised mail was blocked.

It is a label and a set of alerts. It never changes DNS, and it never blocks anything itself. The DMARC policy does the blocking.

## Why

Today the sender table says "treat it as unauthorised" and the app does nothing with that. Every visit shows the same fix for a sender already dismissed, and a sender nobody has reviewed looks the same as one that has been reviewed.

An MSP reviews its senders once, and many of the answers are the same everywhere ("Microsoft 365 is fine"), a few are the same for one client's domains, and the rest are per domain. It then wants to hear only about what changes: a new sender, an authorised sender that starts failing, or spoofed mail that is being delivered.

## Goals

- A decision on a service or an IP, with who made it, when, and an optional note.
- Inheritance: a domain decision beats a group decision, which beats the baseline. Each row in the sender table says where its decision came from.
- A sender nobody has decided on is **Unreviewed**, the default, and needs no row.
- The fix text depends on the decision (see "What a decision changes").
- Two new alerts, both quiet by default and with every threshold configurable.
- The client report counts unauthorised mail that receivers blocked.
- Every change is audited, and changing decisions has its own permission.

## Non-goals

- No DNS changes and no policy changes. Moving to reject stays a separate action.
- No decision editing through the public API in this version. The sources API gets a read-only `decision` field.
- No Dashboard column.
- No automatic decisions. dotMARC never marks a sender on its own.
- No IP decisions at baseline or group level. An address only means something on one domain.

## Anti-goals

- Alert fatigue. Turning this on must not raise an alert for every sender a domain already has.
- Overstating a label. "Unauthorised" is someone's decision, not a finding. The page says who decided, when, and at which level.
- A decision that hides a problem. An authorised sender that is failing gets a stronger fix, never a quieter one.
- A baseline silently changing a domain. A baseline or group decision shows its source, and a domain decision always overrides it.

## Design

### What a decision is keyed on

A decision is on a **service** (for example Mailchimp) or an **IP**:

- When a source is attributed to a service, the decision is made on the service and covers all of that service's sources.
- A source in the Unrecognised row has no service, so its decision is made on the IP, on that domain only.
- A person can also make an IP decision inside an expanded service row, to override the service for one address on that domain.

Service decisions rest on the catalogue's names, so the names are now a stable key. Renaming a service in the catalogue needs a migration of its decisions.

### Levels and which one wins

For one sender on one domain, the first match in this order wins:

1. The domain's decision on the IP.
2. The domain's decision on the service.
3. The domain's groups' decisions on the service.
4. The baseline decision on the service.
5. Otherwise Unreviewed.

A domain in several groups can be given conflicting group decisions. The most cautious one wins: Unauthorised, then Under review, then Authorised. The row says so ("Aurora Retail says Authorised, Brightline Legal says Unauthorised"), and a decision on the domain itself settles it.

A baseline or group decision is a rule for when the service shows up, so it applies to a domain only if that service appears in its reports. Nothing is added to domains that don't use it.

"Reset to inherited" on a domain row deletes the domain's own decision.

### Data

A new table `SenderDecisions`:

- `Id`
- `Scope`: `Baseline`, `Group` or `Domain`
- `GroupId` (set for Group scope) and `DomainId` (set for Domain scope), each cascading with its owner, and a check that the right one is set for the scope
- `Kind`: `Service` or `Ip` (Ip is allowed only at Domain scope)
- `Key`: the service name or the IP text
- `Status`: `Authorised`, `Unauthorised` or `UnderReview`
- `Note` (nullable, up to 500 characters)
- `DecidedBy`: the same actor key the saved dashboard defaults use (Entra object ID, else email)
- `DecidedUtc`

Unique on the scope, its owner and the key (with nulls treated as equal, so there is one baseline row per service). Choosing Unreviewed, or "reset to inherited", deletes the row.

### Where people make decisions

- **A domain's sender table:** each row has a status chip, its source ("from the baseline", "from the Aurora Retail group", or "set on this domain"), and an action menu. Changing it saves a domain decision.
- **Baseline:** a "Sender baseline" section in settings, listing every catalogue service with its baseline status.
- **A group:** a "Sender defaults" section on the group's page, listing the services with the group's override and what it overrides ("baseline: Authorised").

### What a decision changes

| Decision | Failing mail | Fix column says |
|---|---|---|
| Unreviewed | any | today's fix, plus "Not reviewed yet" on the chip |
| Under review | any | today's fix |
| Authorised | none | nothing |
| Authorised | some | "You authorised {service}, but N messages fail DMARC. {today's fix}" |
| Unauthorised | blocked (all failing mail was quarantined or rejected) | "Marked unauthorised by {name} on {date}. Your DMARC policy is blocking it." |
| Unauthorised | some delivered | "Marked unauthorised by {name} on {date}. N messages were still delivered. {policy line}" |

The policy line comes from the domain's DMARC policy: at `none` it says "Your policy is none, so this mail is still delivered. Move to quarantine or reject to block it.", at `quarantine` it says it is going to spam, and at `reject` it says receivers should be rejecting it. The wording follows the existing failure messages: the cause first, then what to do.

### Alerts

Two new alert types, in the existing alert system, with the existing ticket rules and acknowledgement behaviour:

1. **New unreviewed sender.** A source with no decision at any level, first seen on this domain within a set number of days, sending at least a set number of messages. It resolves when a decision is made at any level, including Under review. Existing senders never alert, because they were first seen long ago. They only show as Unreviewed. This is how switching the feature on stays quiet, and a baseline entry for a service silences it everywhere.
2. **Unauthorised mail delivered.** A sender whose decision is Unauthorised, and whose failing mail was delivered (disposition none) in at least a set volume over the window. It resolves when the volume drops below the threshold, for example after the policy moves to quarantine or reject.

Settings, beside the existing suspicious reject settings, all configurable:

- A minimum message volume for each alert (defaults 20 and 10).
- How many days count as "new" (default 7).
- An on and off switch for each alert type.

### Client report

One added sentence per domain when it has any unauthorised mail: "{N} messages from senders you haven't authorised were blocked." If some were delivered, a second sentence says so and points to the policy. Nothing appears for a domain with no unauthorised senders. Wording stays plain, with no jargon.

### Permissions and audit

- A new permission, **Review senders**, is needed to change any decision. Seeing decisions needs the existing domain view permission. Client portal users cannot see or change decisions.
- Review senders applies at each level like this: a person limited to some groups can decide for their own groups' domains and for those groups' defaults, but only someone with no group limit can change the baseline.
- A new audit action `sender.decision_changed`, recording the level, the domain or group, the sender, the old and new status, and the note.

### Failures

Saving a decision that races another person's change keeps the later one and shows who made it. A decision on a service the catalogue no longer has is kept but shown as "{name} (no longer recognised)" so it isn't silently lost.

## Constraints

- One migration, adding `SenderDecisions` only.
- Reads load a domain's decisions with its groups' and the baseline in one query per page load, and resolve them in memory.
- The resolution rule lives in one pure function, so the table, the alerts, the report and the API all agree.
- No em dashes in any copy.

## Testing

- The resolution function: each level winning in turn, an IP decision overriding a service decision, conflicting group decisions resolving to the most cautious, a baseline entry for a service the domain doesn't use changing nothing, and Unreviewed as the default.
- The fix text for each row of the table above, for each of the three policies.
- Alerts: a new sender alerts, an old one doesn't, a decision at any level resolves it, each configurable threshold respected, each alert switch honoured, and the delivered alert resolving after the volume drops.
- Database tests (the existing Postgres fixture): the migration, the unique key including the single baseline row, the scope check, cascading deletes with the domain and the group, and the last write winning.
- Permissions: a viewer and a client portal user cannot change a decision, a group-limited user can change their groups' decisions but not the baseline, and the audit entry is written.
- The report sentence appears only when there is unauthorised mail, and counts blocked and delivered separately.

## Phasing

1. **Phase 1:** the table, the migration, the resolution function with domain-level decisions, decisions in the sender table, the fix text, the new permission and the audit entry.
2. **Phase 2:** the baseline and group levels with inheritance, their settings and group page sections, and the source shown on each row.
3. **Phase 3:** the two alerts and their settings.
4. **Phase 4:** the client report sentence, the read-only `decision` field in the sources API, and an "N senders to review" count on the domain's Overview.

## My calls (to check)

- Inheritance order is domain, then group, then baseline, and conflicting group decisions resolve to the most cautious.
- A baseline or group decision applies only when the service shows up in a domain's reports.
- A group-limited person can decide for their own groups, and only an unlimited one can change the baseline.
- Only senders first seen within the configured number of days (default 7) can raise the new sender alert, so enabling this is quiet.
- Defaults of 20 and 10 messages for the alert volumes are guesses, and they are configurable.
- No API writes and no Dashboard column in version 1.
