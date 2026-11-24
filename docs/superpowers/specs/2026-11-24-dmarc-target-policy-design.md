# DMARC target policy (domain baselines, first slice)

Status: approved direction, 24 November 2026. First feature of 0.14.0, ahead of bulk DNS push slice B. Narrow slice of the [baselines spec](2026-11-20-domain-baselines-security-score-and-dashboard-design.md); the rest of that spec stays in 0.15.

## Intent

Today dotMARC has no declared DMARC policy for a domain. The "DMARC policy weakened" alert compares the live record with the last policy it accepted (`DomainAlertState`), so a domain that has published `p=none` since day one never alerts. The `dmarc` push target writes `p=none` for a new record and otherwise only merges dotMARC's `rua=` into the existing one, so it can never tighten a policy.

The user wants a target DMARC policy, set system-wide, per group and per domain, so that:

1. alerts mean something: a domain below its target raises an alert;
2. bulk DNS push can enforce it: the `dmarc` push writes the effective target, so one batch can move 50 domains to `p=reject`;
3. shortfalls are visible: "N domains below target", with a way to see which.

## Goals

* A target `p` and `sp` at three levels: system, group, domain.
* One shared resolver that gives every domain its effective target and where it came from. Alerts, push, the domain page, the Baselines page and lists all use it; none re-implements the rules.
* A new alert type, "DMARC policy below target".
* The `dmarc` push raises policy to the effective target.
* A single Baselines page with tabs, folding in the existing Sender baseline.
* The group conflict rule is stated explicitly and visibly in the docs and on the Baselines page.

## Non-goals (0.15 or later)

* Baseline profiles (Active, Parked, Alias), preset purpose tags, report expectations, Parked checks (null MX, deny-all SPF, revoked DKIM). Issue #58.
* Full drift views with expected-versus-observed counts beyond the DMARC target. Issue #59.
* Secure score and health distribution. Issue #60.
* DKIM targets and intended-state tracking across pushes. Issue #61.
* Setting targets through the public API.
* Group priority ordering or any conflict rule other than strictest wins.

## Anti-goals

* Upgrading must change nothing. With no target set anywhere, there are no new alerts, the push behaves exactly as today, and no domain shows as below target.
* The push never lowers a policy. A domain stricter than its target is left as it is.
* An unknown result is never reported as a failure. A failed or stale DMARC lookup is "needs checking", not "below target".
* Rules are not duplicated across pages. If two screens could disagree about a domain's target, the design is wrong.

## Decided behaviour

### The target

* A target is a `p` value (`none`, `quarantine`, `reject`) and an optional `sp`. When `sp` is not set, it follows `p`.
* `pct` is not configurable. Meeting a target requires an effective `pct` of 100. A partial rollout is not a target.
* Each level is optional. Unset means "inherit".

### Resolution (decided with the user)

For each domain, in order:

1. **Domain override**, if set.
2. **Group targets**: if the domain is in one or more groups with a target, the **strictest** of those wins (`reject` > `quarantine` > `none`, compared separately for `p` and `sp`). "For now": the user may revisit this rule later, so keep it in one place.
3. **System target**, if set.
4. Otherwise the domain has **no target**.

A group target overrides the system target **even when it is weaker** (user decision). This matches the Sender baseline, where a group's defaults override the system baseline. The system target is a default, not a floor.

The resolver returns the effective target and its source: `Domain`, `Group` (with the contributing group or groups and which one won), or `System`. Strictest-of-groups is resolved per tag, so `p` and `sp` may come from different groups; the source names each.

**The strictest-wins rule must be called out explicitly and visibly** in the docs (a warning-style callout with a worked example of a domain in two groups) and on the Baselines Domain tab (a visible notice near the group targets, not hidden in a tooltip). Wherever a domain's target came from more than one group, show that, for example "Reject, strictest of Contoso and Parked-Estate".

### Meeting the target

A domain meets its target when all of these hold for the last DMARC check:

* observed `p` is at least as strict as target `p`;
* observed effective `sp` (which defaults to `p` when the record has no `sp`) is at least as strict as target `sp`;
* observed effective `pct` is 100.

Stricter than the target counts as meeting it. Outcomes:

* **Meets target.**
* **Below target**: the record was read and fails a condition above, or the domain has no DMARC record (confirmed missing).
* **Needs checking**: the last lookup failed or has not run. Not counted as below target.
* **No target**: nothing applies.

The observed values are already stored on `Domain` (`DmarcPolicy`, `DmarcSubdomainPolicy`, `DmarcPercent`) by the DMARC check, with `sp` and `pct` defaults filled in.

### Alert

* New alert type `DmarcPolicyBelowTarget`, display name "DMARC policy below target". It is a DNS health alert with domain scope, confirmed and resolved the same way as the other DNS health alerts, and auto-closes when the domain meets its target, stops having a target, or stops being monitored.
* The alert text names the observed and target values and the target's source, for example "example.com publishes p=none; its target is p=reject (from group Contoso)."
* It is a rule kind in the existing 0.13 alert rule model and enabled by default. With no targets configured it never fires, so enabling it by default changes nothing on upgrade.
* It is separate from "DMARC policy weakened": that one is about change, this one is about a standard. A domain can raise both.

### Push

`DmarcChangeBuilder` uses the resolver:

* **No target:** unchanged behaviour (create with `p=none`, otherwise merge `rua=` only).
* **New record:** create with the target `p`, and `sp` only when it differs from `p`.
* **Existing record below target:** in the same merge that adds `rua=`, raise `p` and/or `sp` to the target and remove any `pct` below 100. Every other tag is kept as it is.
* **Existing record at or above target:** `rua=` merge only; never lower anything.
* **CNAME-delegated record:** the replacement record uses the target values in place of `p=none`.

This applies to single pushes and bulk batches alike, since both build through the same builder. The plan fingerprint already covers the record value, so a target change between review and push makes the plan differ and the domain is skipped and reported, as designed in slice A.

### Baselines page

A single Baselines entry in the settings menu replaces "Sender baseline". Tabs:

* **`/baselines/sender`**: the existing Sender baseline page (`SenderDefaults.razor`), moved under a tab with its behaviour and permission unchanged. `/senders/baseline` redirects here.
* **`/baselines/domain`**:
  * the system-wide target (`p`, `sp` with "follows p" as the default);
  * the strictest-wins notice;
  * a table of groups with their targets (with the domain count for each);
  * the domains with their own override, linking to each;
  * the count of domains below target, linking to the domain list filtered to them, and the count needing checking.

Each tab shows only to people allowed to change it. Later baseline kinds (0.15 profiles) become further tabs or extend the Domain tab.

### Permission (decided with the user)

One new permission, `BaselinesManage`, covers targets at every level. The level a person can change follows their group scope, as the Sender baseline does (`SenderDecisionAccess`):

| Who holds `BaselinesManage` | System target | Group target | Domain override |
| --- | --- | --- | --- |
| Staff not limited to groups | yes | any group | any domain |
| Staff limited to some groups | no | their own groups | domains in their groups |
| Client portal users | no | no | no |

* Not one permission per level: scope already separates the levels, and it keeps the Domain and Sender tabs on the same model.
* Not reused from `DnsPushManage` (it guards provider credentials) or `DomainsEdit` (too broad). Pushing still needs `DomainsEdit`, so setting a standard and applying it to DNS can be held by different people.
* Named for baselines generally, so 0.15 profiles fall under it. The Sender tab keeps `SenderReview`.
* The Domain tab shows to anyone with `BaselinesManage`. A group-limited holder sees the system target read-only and only their own groups and domains.
* Viewing a domain's effective target needs only `DomainsView`.
* On upgrade, the built-in Admin role gains it automatically (`AccessBootstrapper` syncs locked roles to every permission). Custom roles do not, so nobody gains a power by upgrading. The release notes and `permissions-and-access.mdx` say so, and the Access screen lists it automatically (it shows permission names; the description lives in the docs).

### Group and domain pages

* Groups gains a "Domain baseline" action beside the existing Sender defaults action, to set or clear that group's target.
* The domain page shows the effective target, its source and the outcome (meets, below, needs checking), with an action to set or clear the domain's own override.

### Visibility in lists

* The current domain list gets a "Below target" filter. When the Domain Inventory (roadmap item 13) and unified Filters (item 15) land, the filter moves there with them; the Dashboard problem queue (item 14) lists below-target domains as a configuration problem.

### Docs

* A Baselines docs page covering both tabs, with the Sender baseline content moved or linked and an explicit strictest-wins callout with a worked example.
* `dns-provider-push.mdx` says the DMARC push writes the effective target and never lowers a stricter policy.
* `alert-rules.mdx` (or the alert list) gains the new alert type.
* Release notes and the upgrade note say nothing changes until a target is set.

## Approaches considered

1. **Target fields on the existing records plus one shared resolver (chosen).** Nullable target `p`/`sp` on `DnsRecordSettings` (system), `Group` and `Domain`. Least new machinery and matches how group branding and DNS settings are stored. The resolver is the part 0.15 profiles plug into.
2. **A separate scope-keyed `DmarcTarget` table.** One place for every target, slightly easier to grow into profiles, but a polymorphic scope key has no real foreign keys, so deleting a group or domain needs hand-written cleanup, and settings would read from two places.
3. **A minimal profile entity now.** Closest to 0.15, but reopens the profile naming, assignment and tag questions deferred to 0.15 and fixes the profile model before it is designed.

Scope option B (the whole of #58 and #59 in 0.14) was rejected as too large and blocked on open profile decisions.

## Constraints

* The 0.13 alert rule model, DNS health evaluator and confirmation flow are reused, not forked.
* Audit: setting or clearing a target at any level is recorded with existing audit conventions (who, scope, old and new values).
* EF migration: nullable columns only, no data backfill.
* Demo: the demo data sets a system target and group targets so the Baselines page, the alert and below-target domains show, and a demo bulk push can raise a domain to its target once slice B's demo provider exists.

## Left to the builder (my calls, can be corrected)

* **No double alert for a missing record.** A missing or unreadable DMARC record already raises "DMARC record broken", so the below-target alert stays quiet while there is no readable policy. A record that is broken but still readable raises both alerts. The domain still counts as below target on the Baselines page and in the filter.
* **Ticket default.** `CreatesTicketByDefault: false` for the new alert type: it reports a standing posture, not a breakage, and a team that sets a target for many domains at once should not get a ticket flood.
* **API.** The domain read model gains the effective target and outcome (read-only) and the domain list a `belowTarget` filter, from the same resolver. Writing targets through the API is out of scope.
* **Storage names**, migration name, resolver type name, component structure and tab component choice.
* **Ordering inside the release.** This lands before slice B; slice B's review shows a policy raise in the plan like any other change, with no special handling.

## Acceptance criteria

* With no target at any level, existing behaviour is unchanged: no new alerts, identical push plans, no below-target counts.
* Resolution follows domain, then strictest group, then system, and a weaker group target overrides a stricter system target.
* A domain in groups with `reject` and `quarantine` targets resolves to `reject`, and every surface says it is the strictest of the two.
* Stricter-than-target counts as meeting it; a missing record counts as below; a failed lookup is "needs checking".
* `pct` below 100 counts as below target and is removed by the push.
* The push raises a below-target record to the target, keeps unrelated tags, and never lowers a stricter policy, for single and bulk pushes.
* The below-target alert opens and auto-closes with the domain's state and names its source.
* `/senders/baseline` redirects to `/baselines/sender`, which behaves as before.
* The strictest-wins rule is visible on the Baselines Domain tab and in the docs with a worked example.
* Only `BaselinesManage` holders can change targets, at the levels their group scope allows; a group-limited holder cannot change the system target or another group's target.
* Counts on the Baselines page match the filtered domain list under the same permissions.
