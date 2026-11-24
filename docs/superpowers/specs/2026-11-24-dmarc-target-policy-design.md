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

**Superseded in part by "RFC 9989 alignment" below**, which adds conditions (valid record, `np`, `t`) and changes how invalid records count.

### RFC 9989 alignment (decided with the user, 24 November)

RFC 9989 (May 2026) obsoletes RFC 7489 and is the current DMARC standard. dotMARC judges records against RFC 9989 and takes the cautious reading where RFC 7489 receivers, still in service, would behave differently. Sources: RFC 9989 sections 4.7, 4.8, 4.10, 4.10.1, Appendix A.6 and C; RFC 6376 section 3.2 (DKIM tag-list rule, which DMARC's syntax adopts by reference).

#### What the standard says, as dotMARC applies it

* `v=DMARC1` must be the first tag. If it is absent, not first or not exactly `DMARC1`, receivers ignore the whole record.
* More than one DMARC record at `_dmarc.<domain>`: receivers discard all of them. TXT values that do not start with `v=DMARC1` are not DMARC records and are ignored when counting.
* A repeated tag name makes the tag-list invalid (RFC 6376 section 3.2, adopted by reference; RFC 9989 has no explicit text). dotMARC treats the record as malformed: receivers may discard it.
* A missing or invalid `p`, or an invalid `sp` or `np`: receivers treat the record as `p=none` when it has a valid `rua`, otherwise they apply no DMARC processing (RFC 9989 section 4.10.1).
* `np` (new) is the policy for non-existent subdomains; when absent, `sp` applies, else `p`.
* `t` (new, `y` or `n`, default `n`): `t=y` asks receivers not to apply the policy and to apply one level lower. It replaces `pct=0`.
* `pct`, `rf` and `ri` are removed (registry status "historic"). RFC 9989 receivers ignore them; RFC 7489 receivers still apply `pct`.
* Unknown tags are ignored.

#### Meeting the target (replaces the list above)

A domain meets its target when all of these hold for the last DMARC check:

* the record is valid: exactly one DMARC record, `v=DMARC1` first, no repeated tag, a valid `p`, and valid `sp` and `np` when present;
* observed `p` is at least as strict as target `p`;
* observed effective `sp` (`sp`, else `p`) is at least as strict as target `sp`;
* observed effective `np` (`np`, else `sp`, else `p`) is at least as strict as target `sp`. The target's `sp` covers existing and non-existent subdomains; there is no separate `np` target;
* `t` is not `y`;
* `pct` is absent or 100. A `pct` below 100 stays below target (RFC 7489 receivers apply it partially).

Stricter counts as meeting it. An invalid record is **Below target** with its problem named, never "needs checking". A missing record is Below target as before. "Needs checking" remains only for a domain never checked (see the stale-observation note in the release notes).

#### Record problems shown on the domain page

The DMARC check names each problem. Two kinds:

* **Breaking** (the record may be ignored or read as `p=none`): `v=DMARC1` missing or not first; more than one DMARC record; a repeated tag; a missing or invalid `p`; an invalid `sp` or `np`. A breaking problem sets the DMARC check status to Misconfigured with the problem in its detail, so the existing "DMARC record broken" alert covers it. Release note: domains with genuinely broken records may raise that alert after upgrading.
* **Informational** (the record works but is out of date or not enforcing): `pct`, `rf` or `ri` present (obsolete in RFC 9989); `t=y` (testing mode). These do not change the check status or raise an alert. They show on the domain page and, where they affect the target (`pct` below 100, `t=y`), as the reason the domain is below target.

#### Suggested clean record and push

For any record with a problem, the domain page shows a suggested clean record with a copy action, and the `dmarc` push writes it:

* `v=DMARC1` first, each tag once, other tags kept in their original order.
* A repeated `p`, `sp` or `np` keeps its strictest valid value, so nothing a receiver might have applied is weakened. Any other repeated tag keeps its first value.
* A missing or invalid `p` is written as the strictest valid `p` found, else `none`. An invalid `sp` or `np` is dropped (it then inherits).
* `rf` and `ri` are dropped (no effect on enforcement).
* Without a target, `pct` and `t` are kept as they are (dropping them would change enforcement without anyone asking); they stay flagged as informational.
* With a target, the record is also raised to it: `p`, `sp` and `np` raised (never lowered), `pct` removed, `t=y` removed.
* More than one DMARC record at the name: the push removes every existing DMARC value and writes the one clean record, built from the values combined by the same rules. Non-DMARC TXT values at the name are left alone.
* The review screen labels a repair as "Repairs a malformed DMARC record" and lists each change in plain words (for example "Removed the repeated p=none", "Removed the obsolete pct=50").

This supersedes the earlier "rewrite every copy of a repeated tag" behaviour and replaces the separate raise step. A valid record with no problems and no target still produces exactly today's push plan.

**Not in 0.14:** DMARC discovery by tree walk (a subdomain without its own record inherits its organisational domain's `sp` or `np`, so it should not show as missing); rollout warnings from RFC 9989 section 7.4 (general-purpose mail domains should not publish `p=reject`; a month at `p=none`, then at `p=quarantine`, before `p=reject`); `psd` support.

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
