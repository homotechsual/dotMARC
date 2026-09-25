# Alert ticket rules: which alert types create HaloPSA tickets

Date: 2026-09-25. Status: approved design, not yet planned or built.

## Problem

Every alert dotMARC raises opens a HaloPSA ticket, provided the domain resolves to a Halo client. There is
no way to say "this alert type shouldn't create tickets", or "this client doesn't want that type". Admins
need a global choice per alert type, and a per-client override.

This spec covers ticket rules only. Suggesting groups and domains from Halo data is a separate design that
follows it.

## Decisions

1. **Global default per alert type, plus a per-group override with three states:** inherit, always create,
   never create. A group stores only what it changes.
2. **Which group's override applies:** the group that decides the Halo client, which is the group
   `HaloClientResolver` picks (lowest id among the domain's groups that have a Halo client). A domain with
   its own Halo client override has no deciding group and uses the global defaults.
3. **Storage:** a rules table, not columns or JSON on existing rows, so a new alert type needs no migration.
4. **Scope of effect:** tickets only. The alert is still recorded and the Teams or webhook notification
   still goes out. Closing an existing ticket ignores the rules. Changing a rule is not retroactive.

## Data model

* `AlertTypes`, a code registry of known alert types. Each has a key, a display name, a one-line
  description and a default of "creates a ticket". The four types raised today are `MissedReport`,
  `SuspiciousRejectActivity`, `TlsrptFailure` and `UnexpectedActivityOnNullRoutedDomain`. `AlertingService`
  uses the registry's keys instead of string literals.
* `AlertTicketRule`: `Id`, `AlertType` (the key), `GroupId` (nullable) and `CreateTicket` (bool).
  * A row with no group is the global setting for that type. A row with a group is that group's override.
  * Uniqueness: one row per (alert type, group), and one row per alert type where the group is null. In
    PostgreSQL nulls are distinct in a unique index, so this is two indexes, the second filtered on
    `GroupId IS NULL`.
  * Deleting a group deletes its rules (cascade).
* One migration adds the table and seeds nothing. No rows means every type defaults to creating tickets, so
  behaviour is unchanged until someone edits a rule.

## The decision

A pure function, `AlertTicketPolicy.ShouldCreateTicket(alertType, domain, rules)`:

1. Find the deciding group: none if `domain.HaloClientId` is set, otherwise the group
   `HaloClientResolver` picks.
2. If that group has a rule for the alert type, return its `CreateTicket`.
3. Otherwise, if a global rule exists for the type, return its `CreateTicket`.
4. Otherwise return the registry default (true).

An alert type present in the rules table but absent from the registry is ignored.

It is called from `PsaTicketService.CreateTicketAsync` after the Halo client is resolved and before the
duplicate-ticket check. A "no" returns without creating a ticket. It reads only the rules for the domain's
deciding group and the global rows, in one query.

## User interface

* **Global defaults:** a new **Which alerts create tickets** panel on Alert settings, between the PSA
  settings and the test panel. One row per registry entry with its name, description and a switch. Each
  switch saves immediately with a short confirmation. The panel shows only when HaloPSA ticket sync is
  enabled. A note explains that a domain with its own Halo client override uses these defaults.
* **Per-group overrides:** a **Ticket rules** button on each group on Manage groups, opening a dialog with
  one row per alert type and three choices: *Use default (currently Yes or No)*, *Always create*,
  *Never create*. Choosing *Use default* deletes that group's row. A group with overrides shows a badge such
  as "2 overrides". The button appears only for groups that have a Halo client set.
* **Domains:** no rule UI.
* **Docs:** a section on the PSA integration page and an entry in the Halo FAQ.

Permissions follow the existing pages: whoever can edit Alert settings edits the global rules, and whoever
can manage groups edits group overrides.

## Errors

If reading the rules fails, the existing handler around ticket creation in `AlertingService` logs a warning
and no ticket is created. The alert and its notification are unaffected. Deleting a rule can't fail
partially, since each change is a single row.

## Testing

* Unit tests for `AlertTicketPolicy`: global on and off; a group override in each direction; inherit; a
  domain with its own Halo client override; a group with no Halo client; a type with no rule; several groups,
  where the deciding group's rule wins and another group's does not; a rule for an unknown alert type.
* Service tests: `PsaTicketService` skips the ticket but the alert is still recorded; closing an existing
  ticket ignores the rules; saving, updating and clearing a rule; deleting a group removes its rules.
* A guard test that every alert type `AlertingService` raises is in the registry.
* A browser check of both screens against the throwaway stack, with the fake Halo server.

## Out of scope

Per-domain rules, per-severity rules, retroactive changes to open alerts, and suggesting groups or domains
from Halo data (the next design).
