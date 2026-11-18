# Mark senders as authorised or unauthorised

Builds on `2026-11-18-sender-identification-design.md`, which shows which sending services a domain's reports point to.

## Goal

Let a person record, per domain, a decision about each sender: **Authorised** (it should send as this domain), **Unauthorised** (it should not), or **Under review**. Then use the decision:

- Stop telling someone to fix a sender they have already decided to leave blocked.
- Say plainly whether an unauthorised sender is actually being blocked.
- Raise an alert when a new sender appears that nobody has looked at, or when unauthorised mail is getting through.
- Let the client report say how much unauthorised mail was blocked.

It is a label and a set of alerts. It never changes DNS, and it never blocks anything itself. The DMARC policy does the blocking.

## Why

Today the sender table says "treat it as unauthorised" and the app does nothing with that. Every visit shows the same fix for a sender already dismissed, and a sender nobody has reviewed looks the same as one that has been reviewed.

An MSP reviews a client's senders once, then wants to hear only about what changes: a new sender, an authorised sender that starts failing, or spoofed mail that is being delivered.

## Goals

- One decision per domain per sender, with who made it, when, and an optional note.
- The sender table shows each sender's decision and lets a person with the right permission change it.
- A sender nobody has decided on is **Unreviewed**, which is the default and needs no row.
- The fix text depends on the decision (see "What a decision changes").
- Two new alerts, both quiet by default: a new unreviewed sender, and unauthorised mail being delivered.
- The client report counts unauthorised mail that receivers blocked.
- Every change is audited.

## Non-goals

- No DNS changes and no policy changes. Moving to reject stays a separate action.
- No decisions shared across domains or groups ("Mailchimp is authorised for every domain in this group"). That is the obvious next request and is listed under open questions.
- No decision editing through the public API in this version. The sources API gets a read-only `decision` field.
- No Dashboard column.
- No automatic decisions. dotMARC never marks a sender on its own.

## Anti-goals

- Alert fatigue. Turning this on must not raise an alert for every sender a domain already has.
- Overstating a label. "Unauthorised" is someone's decision, not a finding. The page says who decided and when.
- A decision that hides a problem. An authorised sender that is failing gets a stronger fix, never a quieter one.

## Design

### What a decision is keyed on

A decision is on a **service** (for example Mailchimp) or on a single **IP**, within one domain:

- When a source is attributed to a service, the decision is made on the service and covers all of that service's sources for the domain.
- A source in the Unrecognised row has no service, so its decision is made on the IP.
- A person can also make an IP decision inside an expanded service row, to override the service for one address. An IP decision wins over a service decision.

Service decisions rest on the catalogue's names, so the names are now a stable key. Renaming a service in the catalogue needs a migration of its decisions.

### Data

A new table `SenderDecisions`:

- `Id`
- `DomainId` (cascade delete with the domain)
- `Kind`: `Service` or `Ip`
- `Key`: the service name or the IP text
- `Status`: `Authorised`, `Unauthorised` or `UnderReview`
- `Note` (nullable, up to 500 characters)
- `DecidedBy`: the same actor key the saved dashboard defaults use (Entra object ID, else email)
- `DecidedUtc`

Unique on `(DomainId, Kind, Key)`. Choosing Unreviewed deletes the row.

### What a decision changes

In the sender table, each row shows a status chip and an action menu to change it.

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

1. **New unreviewed sender.** A source with no decision, first seen on this domain in the last 7 days (the same "new source" test the sources table already uses), sending at least a set number of messages. It resolves when a decision is made. Existing senders never alert, because they were first seen long ago. They only show as Unreviewed. This is how switching the feature on stays quiet.
2. **Unauthorised mail delivered.** A sender marked Unauthorised whose failing mail was delivered (disposition none) in at least a set volume over the window. It resolves when the volume drops below the threshold, for example after the policy moves to quarantine or reject.

Settings, beside the existing suspicious reject settings: a minimum volume for each (defaults 20 and 10 messages), and each alert type can be turned off.

### Client report

One added sentence per domain when it has any unauthorised mail: "{N} messages from senders you haven't authorised were blocked." If some were delivered, a second sentence says so and points to the policy. Nothing appears for a domain with no unauthorised senders. Wording stays plain, with no jargon.

### Permissions and audit

- Changing a decision needs the same permission as editing a domain's settings. Client portal users cannot see or change decisions.
- A new audit action `sender.decision_changed`, recording the domain, the sender, the old and new status, and the note.

### Failures

Saving a decision that races another person's change keeps the later one and shows who made it. A decision on a service the catalogue no longer has is kept but shown as "{name} (no longer recognised)" so it isn't silently lost.

## Constraints

- One migration, adding `SenderDecisions` only.
- Reads join decisions to the sender table's result in memory, so no new queries beyond one per page load.
- No em dashes in any copy.

## Testing

- The decision lookup: an IP decision overrides a service decision, and Unreviewed is the default.
- The fix text for each row of the table above, for each of the three policies.
- Alerts: a new sender alerts, an old one doesn't, a decision resolves the alert, volume thresholds respected, the delivered alert resolves after the volume drops.
- Database tests (the existing Postgres fixture): the migration, the unique key, delete cascading with the domain, the last write winning.
- Permissions: a viewer and a client portal user cannot change a decision, and the audit entry is written.
- The report sentence appears only when there is unauthorised mail, and counts blocked and delivered separately.

## Phasing

1. **Phase 1:** the table, the migration, decisions in the sender table, the fix text and the audit entry.
2. **Phase 2:** the two alerts and their settings.
3. **Phase 3:** the client report sentence, the read-only `decision` field in the sources API, and an "N senders to review" count on the domain's Overview.

## Open questions

1. **Shared decisions.** An MSP will want "Microsoft 365 is authorised everywhere" or per group. Do you want that in version 1, or after we've seen how single-domain decisions get used? My proposal is after, because a shared decision needs a rule for when a domain disagrees with its group.
2. **Who may decide.** I have proposed the domain edit permission. A separate "review senders" permission would let a junior mark things without editing settings. Is that wanted?
3. **Under review.** Is it worth having as a third state, or is Authorised, Unauthorised and Unreviewed enough? I kept it because "I've seen this, I'm asking the client" is a real state, and it stops the new sender alert re-firing.
4. **Alert defaults.** The 20 and 10 message thresholds are my guesses.

## My calls

- Decisions are per domain, keyed on the service name or the IP.
- Only senders first seen in the last 7 days can raise the new sender alert, so enabling this is quiet.
- No API writes and no Dashboard column in version 1.
