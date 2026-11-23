# Domain baselines, secure score and dashboard workflow

Status: requirements draft with a phased delivery plan. The 0.14 subset is locked below and in the roadmap; baseline contracts remain draft. This document records scope, not an instruction to implement the entire feature set.

## Locked 0.14 subset (22 November)

The user accepted bulk DNS push plus the action-centre Dashboard, separate Domain Inventory and correctness/UX fixes:

- Existing-fact summary counts, a domain problem queue and seven-day quarantine/reject chart with a matching investigation queue.
- Full inventory on its own page with viewer access, grouped views, sorting, preferences, return navigation and authorised bulk selection preserved.
- Unified Filters on Dashboard and Inventory, dashboard sender-review visibility, collapsed polling details with visible operational failures, and Add domain controls on Dashboard and Import.
- Existing roadmap improvements: tag management page, report-based DKIM suggestions, overview group assignment, review/per-IP action fixes, percentage layout, API scope protection and list-position restoration.
- Null-domain DMARC/DKIM corrections using existing observed evidence, independently of new baseline profiles. Do not suppress genuine policy weakening or treat incomplete lookups as proof of absent keys.

Problem-queue membership is confirmed DNS failures, overdue expected reports and outstanding sender decisions; unknown required checks are separately labelled "needs checking". Operational failures appear first, then domain issues by severity. Entries link directly to an action. Quarantine/reject traffic remains a separate investigation reason, not automatically a configuration failure.

Use seven days for quarantine/reject counts and matching drill-downs, labelled 30-day pass rates, and Domain Overview's existing sender-review window. Keep polling details collapsed on Dashboard and surface existing PSA/polling problems for authorised viewers; per-rule ticker configuration is not in this release.

Assigned to 0.15 in the subsequent release-issue planning discussion: baseline profiles/preset purpose tags, drift views, secure score and health-distribution chart, managed overrides that generate baselines, provider-wide selector enumeration, domain-age conditions, Forwarder decisions and per-rule dashboard visibility. The later sections retain their open design decisions; release assignment does not make them 0.14 dependencies or settle their behavioural contracts.

See the [locked roadmap](../plans/2026-11-20-v0-14-0-roadmap.md) for sequencing and release gates. Routes, component choices and preference-migration mechanics remain detailed-plan decisions within this scope.

## Intent

Make domain health reflect what a domain is intended to do. A deliberately non-sending domain must not be treated as a broken sending domain. Use the same expectations for DNS checks, report expectations, alerts and a dashboard secure score.

Also simplify domain onboarding, dashboard status, tag management and filtering.

## Agreed direction

- Named, reusable baseline profiles describe domain purpose, expected reports and required DNS/security checks.
- Profiles can be assigned to groups or individual domains. An explicit domain assignment overrides group assignments.
- Provide Active, Parked and Alias profiles with matching default tags.
- Calculate the secure score against each domain's effective baseline, not a universal requirement for every domain to publish sending keys and MTA-STS.
- Make baseline drift discoverable through counts, expected-versus-observed values and domain drill-downs.
- Allow per-domain and per-group overrides and DNS pushes for both DMARC record values and DKIM selectors/records.
- On explicit confirmation of a managed change, automatically create and assign the intended baseline; do not wait for push success or DNS verification.
- Move the full domain inventory to its own page; Dashboard is the action centre, not the complete domain list.
- Record requirements and plan only at this stage; do not change application behaviour.

## Related work

- [Null-routed domains and mail-service detection](2026-09-07-null-routed-domains-and-mail-service-detection-design.md).
- [DNS health alerts](2026-10-02-dns-health-alerts-design.md).
- [Alert rules](2026-11-19-alert-rules-design.md).
- [DKIM selector detection from reports](2026-11-20-dkim-selector-detection-design.md).
- [Current roadmap](../plans/2026-11-20-v0-14-0-roadmap.md).

The earlier null-routing design derives observed non-sending status from SPF and explicitly excludes configurable intent and policy-strength checks. This draft proposes extending that model: intended purpose is a profile; observed DNS remains evidence. Do not silently reinterpret the old design as already implementing this contract.

The DKIM design currently proposes report-based suggestions first and excludes speculative probing. DNS-provider enumeration below is additional proposed work, not an already approved replacement for that design. This document does not expand the current release roadmap automatically.

## 1. Baseline profiles and default tags

| Profile | Intended behaviour | Report expectation | Security expectations |
| --- | --- | --- | --- |
| Active | Sends mail | Reports expected | Valid DKIM and DMARC; MTA-STS when receiving mail is expected |
| Parked | Neither sends nor receives mail; null routed | No missing-report alert by default | Reject DMARC, deny-all SPF, null MX and no usable DKIM keys |
| Alias | May send mail at low volume | No missing-report alert by default | Valid DKIM and DMARC; MTA-STS when receiving mail is expected |

Receiving-mail expectations must be represented explicitly rather than inferred from the word Alias. Do not equate low volume with an outage.

A baseline is a declared expectation, not simply a snapshot of current DNS and not the same thing as an alert evaluator's remembered historical baseline.

### Assignment contract

- A domain uses its explicit profile when one is assigned; otherwise resolve its group profile assignments.
- Resolve effective expectations once through a shared contract consumed by checks, alerts and reporting. Do not duplicate resolution rules across pages.
- Tags and baseline assignments must not become competing sources of truth. The exact relationship between preset tags and profile assignment remains an open decision.
- Multiple conflicting group assignments, the default for unassigned domains and the migration of existing domains require explicit decisions before implementation.
- Applying a profile must not publish, remove or otherwise modify DNS records.

### Drift discovery

Users must be able to find domains that differ from their effective baseline without opening each domain.

- Provide a compliance/drift view scoped by baseline, group, tag and domain, integrated with the unified filter model.
- Show counts by expected and observed values. For example: baseline requires `p=reject`; 12 domains publish `p=none`, three publish `p=quarantine`, two have no record, and one could not be checked.
- Let users select a count to see the affected domains, their effective baseline and its assignment source, expected values, observed values and observation time.
- Keep missing, invalid, unchecked, stale and lookup-failure states distinguishable from a valid but different value.
- Use semantic record comparisons and protocol defaults, not raw TXT string equality. Reordered equivalent tags must not appear as drift.
- Use the same comparison results for drift, alerts and secure score. Exact-match drift and security weakening are distinct: a stronger-than-required value may differ without being weaker. Define which baseline requirements are exact values versus minimum strengths.
- Apply access restrictions before aggregation and de-duplicate domains across groups.

### Acceptance criteria

- Editing a reusable profile updates effective expectations for its assigned domains and groups without copying profile values into each domain.
- An explicit domain profile overrides a conflicting group profile consistently in alerts and score calculations.
- Active expects reports; Parked and Alias do not raise missing-report alerts by default.
- Changing purpose does not falsify stored DNS observations or silently weaken published security policy.
- A `p=reject` baseline exposes the count and list of domains actually publishing `p=none`, separately from missing or unknown results.
- Every aggregate drills down to a consistent, authorised domain list without double-counting group membership.
- Changes to baseline assignments and verified DNS observations are reflected in drift without treating desired values as observed values.

## 2. Null-domain checks and alerting

Investigate the reported DMARC-policy-weakened alert for null domains end to end: DNS parsing, stored observations, historical policy baselines, rule evaluation, confirmation and alert resolution. A parked domain still needs strong DMARC; do not fix misleading alerts by suppressing genuine policy weakening for all parked domains.

Evaluate DKIM against purpose:

- Sending profiles need usable signing keys for the selectors they are expected to use.
- For Parked, an absent selector record supplies no key; an empty `p=` revokes a key. Neither should be reported as a missing sending key.
- A remaining usable TXT key or CNAME delegation to a usable key must be surfaced for Parked.
- DNS lookup failure, inaccessible provider data and incomplete discovery are not proof that no usable keys exist.
- Resolve or re-evaluate existing alerts when expectations change, following existing alert lifecycle and confirmation conventions.

### Standards and discovery limits

[UK government guidance for non-sending domains](https://www.gov.uk/guidance/protect-domains-that-dont-send-email) recommends deny-all SPF, reject DMARC, null MX, `*._domainkey TXT "v=DKIM1; p="`, and revoking existing TXT and CNAME selectors. A wildcard does not override existing explicit selector records.

Null MX declares no inbound mail; it does not by itself prove a domain never sends. Parked domains may still receive DMARC reports about spoofing. Do not treat report arrival alone as proof of legitimate sending.

Ordinary DNS queries cannot enumerate all records under `_domainkey`; neither an ANY query nor a wildcard query provides that inventory. Consequently, absence at known selectors cannot prove domain-wide absence of signing keys.

### Acceptance criteria

- A correctly configured Parked domain does not fail solely because DKIM keys are absent or revoked.
- A usable key at a known selector is reported as conflicting with Parked intent.
- A valid reject policy is not reported as weakened solely because of null routing; a real downgrade still alerts.
- Temporary or incomplete lookups cannot produce a secure result.
- Reclassification handles outstanding alerts without leaving misleading active warnings.

## 3. DKIM selector discovery

Requested capability: automatically obtain selector records where the available evidence permits it.

- Connected DNS-provider APIs can enumerate accessible zone records and identify TXT and CNAME selectors under the domain's `_domainkey` namespace.
- Incoming DMARC authentication results provide observed signing domains and selectors, using the existing report-detection design.
- Preserve manual configuration and record provenance. Discovery must not silently overwrite or prune administrator-managed selectors.
- Distinguish an enumerated zone inventory from report observations; reports are not a complete DNS inventory.
- Surface inaccessible zones, pagination failures and incomplete discovery.
- Common-selector probing is not a completeness mechanism and is not included by default.

Before implementation, decide whether discovery initially suggests additions or automatically maintains a separate discovered set, and how wildcard records and selector rotation affect checks.

### Acceptance criteria

- Provider discovery includes TXT and CNAME records, respects zone/domain boundaries and consumes all result pages.
- Report discovery associates selectors with their signing domain, not merely the domain whose report contained them.
- A discovery failure preserves existing configuration and is visible.
- The UI does not claim that all selectors were checked when evidence is incomplete.

## 4. Managed DMARC and DKIM changes

Users confirmed that overrides cover both DMARC policy/record values and DKIM selectors with their TXT/CNAME records. DMARC itself has no selector: its policy record is normally at `_dmarc.<domain>`.

- Support overrides and pushes from dotMARC for an individual domain or a group of domains.
- Reuse existing single-domain and bulk DNS-push planning, review, authorisation and provider mechanisms. This is not a separate DNS write engine.
- Present the resolved per-domain changes and baseline effects before confirmation, including existing domain overrides and affected group members.
- On explicit confirmation, automatically create and assign a baseline representing the intended configuration. Persist that intent before dispatching DNS writes; a baseline persistence failure must prevent the push from starting.
- For a group operation, preserve shared intent at group scope and domain-specific record values at domain scope as needed. Do not copy one domain's DKIM key or CNAME target to every group member.
- A domain-specific override must not silently edit a shared profile used by other domains.
- Preserve unrelated existing DMARC tags and reporting destinations through the existing merge/review conventions unless their removal is explicitly selected.
- Keep desired state, push state and verified DNS observations separate. A confirmed intention or successful provider response is not proof of published compliance.
- Surface partial group failures and propagation-pending states per domain. Failed or unverified domains must not count as compliant solely because their desired baseline was created.
- Retrying a failed push must not create duplicate baseline assignments or silently move the target to a newly observed configuration.
- Record who confirmed the change, target scope, intended baseline and per-domain push outcome through existing audit conventions.
- Baseline assignment by itself remains read-only with respect to DNS; only the separately reviewed and confirmed push authorises writes.

The exact representation of generated baselines (named profiles, revisions or scoped overrides) and group membership behaviour must be settled before implementation. Baselines must retain any other required checks rather than replacing the entire profile with only the fields changed by the push.

### Acceptance criteria

- A user can change DMARC policy or DKIM selector/record configuration for one domain, review it and confirm a push that also establishes its intended baseline.
- A group change previews each target, respects explicit domain overrides according to the agreed policy and reports partial success without a group-wide false success.
- Cancelling before confirmation creates neither a baseline nor a DNS write.
- Confirmation creates the intended baseline even if a subsequent provider write fails; drift remains visible until observations satisfy it.
- Successful writes remain unverified until the relevant DNS checks confirm them.
- Shared-profile users outside the selected scope are unaffected by a domain override.
- Equivalent retries do not duplicate intent, assignments or unintended DNS changes.

## 5. Domain-age conditions on alert rules

Add a configurable condition based on how long ago a domain was added, suitable for onboarding grace periods.

- Define whether age uses the existing `FirstSeenUtc` or a separate date, including imported and auto-discovered domains.
- Define supported comparisons, time units, boundary semantics and interaction with existing scopes.
- Apply the condition consistently across applicable domain-based rule evaluators, validation, editor, persistence and API.
- Decide what happens to existing alerts when a rule's age condition stops matching.
- Preserve behaviour for rules with no age condition.

### Acceptance criteria

- Tests cover just before, exactly at and just after the configured threshold with a controlled clock.
- Import, manual creation and auto-discovery follow the documented date semantics.
- UI and API reject invalid conditions; unrelated/global alerts are not accidentally excluded.

## 6. Secure score

Display how many monitored domains are configured correctly for their intended purpose:

`secure score = domains passing every required baseline check / monitored domains in scope * 100`

- Show the percentage and passing/total counts.
- Identify unchecked or indeterminate domains separately; keep them in the denominator and do not count them as secure.
- A not-applicable check is not a failure. A required check that has not run is not a pass.
- Use the effective baseline and shared check interpretation, not a second dashboard-only definition of security.
- Apply the same domain scope and filters as the dashboard, within the viewer's authorised access.
- Explain failures and uncertainty per domain. This is a configuration-compliance score, not a guarantee of protection.
- Do not invent a percentage when there are no monitored domains in scope; agree the empty-state wording.

Exact DMARC requirements for sending profiles, DKIM evidence completeness, freshness limits and MTA-STS validation requirements remain to be defined. Do not mark those choices as settled by the formula.

### Acceptance criteria

- Mixed Active, Parked and Alias domains use their own requirements in one score.
- Required failures and unknowns prevent a domain counting as secure; non-applicable checks do not.
- Counts and percentages agree after filtering, group scoping and permission checks.
- Group membership does not count a domain more than once.
- Empty, unchecked and stale-data states are explicit.

## 7. Dashboard and management workflow

### Action-centre direction (22 November mock-up)

The Dashboard should answer "what needs my attention, and what do I do next?" rather than primarily listing every domain. The user's mock-up proposes summary cards, a seven-day quarantine/reject chart, a domain-health distribution, a Domains with Problems list and a Domains with Quarantines/Rejects list. Moving the full domain inventory to its own page is confirmed.

Treat the mock-up as a workflow reference, not a pixel-exact layout or a replacement for the agreed baseline and filtering contracts.

#### Summary and operational context

- Keep compact summary cards for monitored domains, overall pass rate, domains with warnings, missing expected reports, unparseable messages and report sources. Label their time windows and scope explicitly.
- Make actionable counts open the corresponding filtered list or investigation view.
- Show operational alerts, including PSA connection and mailbox polling failures, without making the full polling diagnostic block dominate the page.
- Keep Add domain and a clear route to the full domain inventory available.
- Use one Filters entry point for groups, tags and other filters, rather than reproducing the mock-up's separate Groups and Tags buttons.

#### Domains with Problems

- List domains with actionable configuration failures, baseline drift, missing expected reports or outstanding sender decisions. Healthy domains belong in the inventory, not this queue.
- Show the reason for inclusion and the next action: configure/check DNS, review a sender, investigate missing reports or inspect a failed push.
- Include domain name, purpose/baseline, report status, last report, pass rate with its window, and the relevant DNS/security status. Do not require every column from the mock-up at narrow viewport sizes.
- Make the default order prioritise actionable problems; define severity and tie-breaking before implementation rather than sorting solely by domain name.
- Keep indeterminate/unchecked required checks visible as "needs checking", distinct from confirmed failures.
- Evaluate report expectations and applicable checks against effective intent. A healthy Parked domain must not enter the queue because it has no reports, usable DKIM key or MTA-STS.
- An intentional reporting destination different from dotMARC is a problem only when it violates the effective reporting requirement, not solely because it differs.
- Provide explicit all-clear and no-data states; an empty queue must not imply health if required checks have not run.

#### Domains with Quarantines/Rejects

- Show quarantine and reject message totals over the last seven days, with a domain-level investigation list using that same window.
- Include affected domains with non-zero volume even when their DNS configuration meets the baseline. Configuration compliance and traffic requiring investigation are separate views.
- Show message counts, relevant pass/alignment rates and available sender/reason evidence; drill through to the corresponding domain, time window and records.
- Do not label all quarantine/reject traffic as a configuration failure or automatically recommend weakening policy. Enforcement may be working correctly against spoofing, and authorised forwarding can produce failures requiring a different investigation.
- Count messages rather than reports/records and avoid duplicate domain rows unless an explicit secondary grouping is selected.
- Permit a domain in both queues when it has both configuration problems and traffic to investigate; do not deduplicate away either reason for attention.

#### Domain-health distribution

- The mock-up's Healthy / 1 issue / 2 or more issues chart complements the secure-score percentage; it does not replace its baseline-aware definition.
- Define an issue as a distinct unmet required baseline check, not the number of alerts, rules or delivery attempts. Multiple alerts about one failed check must not inflate the issue count.
- Keep unknown/unchecked required evidence separately identified. A domain with unknown required checks cannot fall into Healthy just because no failure is confirmed.
- Do not mix sender-review counts, quarantine/reject volume or global polling failures into a domain configuration score.
- Display counts alongside percentages and allow each category to open its corresponding domain list.

#### Separate domain inventory

- Move the full domain list off Dashboard to a dedicated Domain Inventory page. Provide clear navigation between the action centre and inventory; choose its route and whether to extend an existing domain-management surface during implementation planning.
- Preserve read access for viewers who cannot manage domains; moving inventory must not make it write-permission-only.
- Preserve group views, sorting, filters, saved defaults, list-position restoration, domain navigation and authorised bulk DNS selection/review.
- Specify how existing dashboard preferences and links migrate when the full table moves. Do not silently discard saved views or orphan return-to-list navigation.

#### Release boundary

Confirmed for 0.14: an action centre and investigation queues using existing facts, with the inventory move coordinated with bulk-push UI and list-position work.

The full baseline-aware score, drift comparisons and managed-baseline changes are deferred. Omit the secure score and domain-health distribution chart in 0.14 rather than substituting an approximation. A release scope change requires agreement; the mock-up does not bring the entire spec into 0.14.

#### Acceptance criteria

- A user can identify an affected domain, understand why it needs attention and reach the relevant action directly from the Dashboard.
- Healthy domains are excluded from the problem queue; required unknown checks remain visible without being labelled confirmed failures.
- A baseline-compliant domain with rejected spoofing traffic appears in the investigation list without being falsely marked misconfigured.
- Seven-day chart totals match the investigation list's message counts under identical filters and access scope.
- Health-chart categories and secure-score counts agree on configuration results, while unknowns remain explicit.
- Moving inventory preserves viewer access, domain navigation, saved views and bulk workflows.
- Every card, queue and chart declares its reporting window and scope; global operational counts are not disguised as group-filtered metrics.
- The layout remains usable with keyboard navigation, narrow viewports, long domain names and no-data/all-clear states.

### Dashboard alerts

- Let alert rules opt into a dashboard ticker/status strip.
- Show PSA-connection and mailbox-polling problems there by default.
- Reuse alert state and rule evaluation rather than creating a parallel alert engine.
- Decide ticker behaviour for delivery-disabled rules, acknowledgements, resolved alerts, ordering and dismissal.
- Avoid duplicate ticker entries and prevent exposure of alerts outside a viewer's scope. Global connection/polling details need an explicit permission policy.
- Keep operational errors readable and accessible; continuous animation is not required.

### Polling status

For 0.14, reduce the dashboard polling block to a simple status icon with details collapsed by default on Dashboard. Preserve access to errors, last-poll information and authorised diagnostic actions. Relocation to email/report settings is not part of this slice.

### Domain onboarding

Add an Add domain box/control on the Dashboard and Import page. Reuse existing domain validation and creation logic, honour write permissions, and define how group/profile selection and import defaults are carried into creation.

### Tags

Move tag management to its own page and provide navigation to it. Preserve permissions, existing tag relationships and assignment workflows.

### Unified filters

Use one Filters button, including in the grouped view. Move Group and Tag controls into the same filtering surface as other dashboard filters. Preserve saved defaults, clear/reset behaviour, active-filter visibility and authorised group restrictions.

### Pass-rate layout

Keep the complete percentage, including `100.0%`, on one line at supported viewport sizes. The supplied screenshot shows the percent sign wrapping beneath the number. Preserve readability rather than hiding or clipping the value.

### Acceptance criteria

- PSA and mailbox-poll failures appear in the default dashboard alert surface for authorised viewers.
- Rule-controlled ticker visibility does not change email/webhook/PSA delivery unintentionally.
- Grouped and ungrouped views expose one consistent filter entry point; saved preferences still work.
- Domain creation works from both requested locations without bypassing validation or permissions.
- Tags have a dedicated reachable management page.
- Polling failures remain apparent when details are collapsed or relocated.
- The pass-rate value stays together at narrow and wide supported layouts.

## 8. Sender review visibility and forwarding decisions

### Consistent review actions

- On Domain Overview, make the "senders need a decision" Review action look and behave like the application's other action buttons, rather than unstyled action text.
- Align the information icon, message and Review button correctly in the alert. The supplied screenshot shows the icon vertically misaligned relative to the message/action row.
- Preserve keyboard access, focus indication and usable layout when the message wraps or the viewport narrows.
- Take Review directly to the relevant sender decisions, rather than making users locate the section after navigation.

### Dashboard review indicator

Requested direction: expose domains with senders needing a decision on the Dashboard using an icon/status indicator.

- Provide an accessible label and tooltip with the outstanding count; do not rely on icon shape or colour alone.
- Link the indicator to that domain's sender review view.
- Reuse the same unresolved-decision definition and reporting window as Domain Overview, including effective IP/service decisions and inherited decisions. Do not introduce a separate dashboard count that can disagree.
- Honour domain access and `SenderReview` permissions. Visibility of the status and the ability to change decisions are separate concerns.
- Decide the indicator's placement and whether an outstanding-sender filter belongs in the unified Filters surface.

### Discoverable per-IP decisions

The current sender-services table says "Decide per IP, below" while its IP controls may be hidden inside a collapsed row.

- Replace the misleading static instruction with an explicit, accessible way to reveal source IPs, such as a labelled "Review source IPs" action.
- Make expanded/collapsed state clear and show per-IP Decide/Change controls where the user has `SenderReview`.
- For read-only viewers, use wording appropriate to viewing source IPs rather than promising a decision action they cannot perform.
- Keep permission enforcement in the decision service/action boundary, not just in button visibility.

### Forwarder override

Add a way to explicitly classify a sending source IP as an authorised forwarder for a monitored domain, alongside the existing sender-decision workflow.

- "Forwarder" describes the source's authorised forwarding role, not a new Active/Parked/Alias domain purpose and not a claim that the monitored domain itself forwards mail.
- Make the classification visible on the IP's decision chip/details and wherever its effective sender decision is summarised.
- Preserve existing decision scoping, precedence, inheritance, permissions and audit conventions. Explicitly decide whether service-level and group/global forwarding decisions are also supported.
- An authorised-forwarder decision must not turn failed SPF, DKIM or DMARC observations into passes or inflate the secure score.
- Keep the distinction between authorised original sending and authorised forwarding in explanatory text. A known forwarder can still appear in failing DMARC reports.
- Before implementation, define whether Forwarder is an authorised decision subtype/role or a separate status, and specify its effects on sender alerts and recommended fixes. Do not assume it should suppress every alert involving that IP.

### Acceptance criteria

- Domain Overview presents a recognisable Review button, with the icon, message and action aligned at supported viewport sizes.
- The dashboard indicator and overview agree on the outstanding sender count and open the relevant domain's review view.
- A collapsed unrecognised-service row offers an obvious way to reveal IPs; authorised reviewers can then decide per IP.
- Read-only users can inspect permitted information but cannot submit decisions.
- A reviewer can classify an IP as Forwarder, see the classification after reload and change it through the existing review workflow.
- Forwarder classification preserves underlying authentication results and follows the agreed inheritance and alert semantics.

## Delivery plan

This is sequencing, not an execution-ready task list. Resolve each slice's open decisions and inspect its existing code before writing a detailed implementation plan.

1. **Define the contracts.** Decide profile fields, assignment conflicts, migration/defaults, tag relationship, receiving expectations and exact security criteria. Specify shared effective-baseline, drift and health results, plus generated-baseline and push lifecycle contracts.
2. **Implement baseline persistence and management.** Add assignments, built-ins, permissions, audit and API/UI coverage using repository conventions. Provide an upgrade path that does not silently reclassify domains.
3. **Correct null-domain evaluation and alerts.** Reproduce the reported DMARC alert, add purpose-aware DKIM interpretation and integrate profile expectations throughout existing alert lifecycles.
4. **Extend selector discovery.** Reuse report-based detection and add read-only provider enumeration as agreed. Keep discovery completeness explicit.
5. **Expose drift and managed changes.** Add expected-versus-observed counts and drill-downs using shared comparisons. Extend reviewed domain/group DMARC and DKIM pushes to persist intended baselines on confirmation; test cancellation, persistence failures, partial writes, retries and verification.
6. **Add domain-age rule conditions.** Implement and test the agreed semantics across evaluators and editing boundaries. This can proceed independently once its date/condition contract is settled.
7. **Add the secure score.** Consume the shared baseline/health model after check semantics and discovery evidence are defined.
8. **Reshape dashboard and management workflows.** Build the action-centre summary, problem and investigation queues, baseline-aware health distribution and separate domain inventory. Add ticker configuration/defaults, simplify polling, add domain entry, separate tag management, unify filters and fix percentage wrapping. Coordinate with bulk-push UI and list-position work; agree the 0.14 subset before implementation.
9. **Improve sender review and forwarding decisions.** Fix Review button consistency, alert alignment and per-IP discoverability; add the dashboard review indicator. Define and implement Forwarder decision semantics through persistence, resolution, presentation and affected alerts without changing reported authentication outcomes.
10. **Verify and document.** Run focused tests for each slice, migration/upgrade tests where applicable, permission/API tests and browser checks. Document purpose-specific expectations and discovery limitations.

The percentage layout fix and dedicated tag page may ship independently. Do not couple them unnecessarily to baseline schema work.

## Decisions required before implementation

- Multiple group profiles: conflict rejection, explicit priority, or another deterministic resolution?
- Which profile applies to unassigned and existing domains, without disruptive automatic reclassification?
- Are preset tags informational, assignment shortcuts, or derived labels? Can they be renamed/deleted?
- How are receive-mail intent and low-volume expectations configured?
- What exact DMARC policy/alignment/report-destination checks constitute a pass for each profile?
- Which baseline fields require exact equality versus a minimum security strength, and how is stronger-but-different configuration presented?
- How are automatically created baselines named, versioned and assigned while preserving unrelated profile requirements?
- For group pushes, how do explicit domain overrides, later membership changes and per-domain DKIM values interact with the generated baseline?
- Is a wildcard revoked DKIM record required or recommended for Parked, and what evidence is sufficient to count its DKIM requirement as passed?
- Which failed/stale/incomplete observations make a check indeterminate, and how long is evidence fresh?
- Discovery suggestions or automatic maintenance first; what provider access and selector-retirement rules apply?
- Domain-age date, comparisons, units and alert-resolution semantics?
- Ticker visibility/lifecycle/permissions, especially for global operational alerts?
- Detailed filter/preference migration mechanics for the locked Dashboard and Inventory scope?
- What route/existing surface hosts the confirmed separate Domain Inventory page?
- Severity tie-breaking and migration of dashboard preferences/navigation to inventory; distinct health-chart issue categories for the later baseline tranche?
- Dashboard sender-review indicator placement and an optional outstanding-decision filter?
- Forwarder as an authorised subtype/role or a distinct status; supported scopes, inheritance, sender-alert treatment and recommended fixes?
- Detailed implementation slices for locked 0.14 work; release allocation for the deferred tranche?

## Out of scope unless separately approved

- Automatically changing DNS when assigning a profile.
- Claiming exhaustive DKIM discovery from ordinary DNS queries.
- Treating zero observed mail as proof of a secure or non-sending domain.
- Replacing existing notification channels or broadening PSA ticketing behaviour.
- Application implementation as part of this documentation task.
