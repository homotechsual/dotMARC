# Sender Decisions, Phase 3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raise two alerts from sender decisions: one when a new sender appears that nobody has looked at, and one when mail from a sender marked Unauthorised is being delivered. Both have configurable thresholds and on/off switches, and neither fires for senders a domain already had.

**Architecture:** A pure evaluator turns a domain's 30-day reports, its decisions and a few settings into two lists of findings. The existing alerting cycle in `AlertingService` calls it for each monitored domain and raises or resolves the alerts through its existing `EnsureAlertAsync` and `ResolveAlertAsync`, so ticket rules, cooldown, webhooks and acknowledgement all apply unchanged. Five settings columns are added to the existing settings row and surfaced on the Alert settings page.

**Tech Stack:** C# / .NET 10, EF Core with Npgsql, Blazor Server + MudBlazor 9, xUnit with the Postgres fixture.

**Spec:** `docs/superpowers/specs/2026-11-18-sender-decisions-design.md` (Alerts). Phases 1 and 2 are done: `docs/superpowers/plans/2026-11-18-sender-decisions-phase-1.md` and `-phase-2.md`.

## Global Constraints

- New alert type keys, stored and never changed: `NewUnreviewedSender` ("New sender to review") and `UnauthorisedSenderDelivered` ("Unauthorised sender mail delivered").
- Settings, all on the existing `NotificationSettings` row with these defaults: `NewSenderAlertEnabled` true, `NewSenderMinVolume` 20, `NewSenderDays` 7 (allowed 1 to 30), `UnauthorisedDeliveredAlertEnabled` true, `UnauthorisedDeliveredMinVolume` 10. A column's default must also apply to the row that already exists, so configure each with `HasDefaultValue` the way the suspicious reject settings were.
- Ticket defaults (my call): `NewUnreviewedSender` does not create tickets by default, `UnauthorisedSenderDelivered` does.
- A source is **new** when it was first seen on that domain within the last `NewSenderDays` days. A service is new only if every one of its source IPs is new, so a known service gaining an IP is not a new sender. An unrecognised IP is judged on its own. Resurfacing after a long quiet spell is not new, because first seen means the first time ever.
- A sender is **unreviewed** when no decision applies at any level (Under review counts as decided). A service row is judged by its service-level decision, and an unrecognised IP by its IP decision.
- **Delivered** means messages with disposition None, whether they pass or fail DMARC, from a sender whose decision is Unauthorised. (This extends the spec's "failing mail", for the reason in Phase 1's fix text: an unauthorised sender whose mail passes is the worse case.)
- Null-routed domains (`SpfCheckStatus.NullSpf`) get neither alert. They already have their own unexpected-activity alert.
- Messages: the cause first, then what to do. Counts use thousands separators. No em dashes. Meaningful variable names, even in tests.

## Review Focus

- Enabling this on an install full of existing senders: no `NewUnreviewedSender` alert at all, because every source was first seen long ago.
- A known service gaining a new IP, and a source that reappears after being quiet: neither is new.
- A decision made at any level (including Under review) resolves the new-sender alert on the next cycle.
- Turning an alert off in settings resolves any alert of that type that is open, instead of leaving it dangling.
- An unauthorised sender delivering fewer messages than the threshold: no alert, and an open one resolves.
- An aligned Microsoft 365 or Google sender (found only by IP owner and selector) judged the same way the Sources tab judges it, so the alert never names a "new unrecognised IP" the table calls Microsoft 365.

---

### Task 1: Settings, migration and the two alert types

**Files:**
- Modify: `src/DotMarc/Notifications/NotificationSettings.cs`, `src/DotMarc/Notifications/AlertTypes.cs`, `src/DotMarc/Data/DotMarcDbContext.cs` (settings configuration, near where the suspicious reject columns are configured)
- Create: `src/DotMarc/Migrations/<timestamp>_AddSenderAlertSettings.cs`, its `.Designer.cs`, and the model snapshot update (generated)
- Test: `test/DotMarc.Tests/Notifications/AlertTypesTests.cs` (add), `test/DotMarc.Tests/Notifications/SenderAlertSettingsTests.cs` (new)

**Interfaces:**
- Produces: the five properties on `NotificationSettings`, and `AlertTypes.NewUnreviewedSender`, `AlertTypes.UnauthorisedSenderDelivered` registered in `AlertTypes.All`.

- [ ] **Step 1: Write the failing tests.**
  - In `AlertTypesTests`: `TheSenderAlertTypes_AreRegistered_WithTheirDefaultsForTickets` (new sender: no ticket by default; unauthorised delivered: ticket by default) and keys unique.
  - In `SenderAlertSettingsTests` (Postgres, migrated database): `ANewInstall_StartsWithTheSenderAlertDefaults` (the seeded settings row reads 20, 7, 10 and both switches on) and `AnExistingSettingsRow_GetsTheDefaultsToo` (insert a row without the new columns by running the earlier migration state, or assert the column defaults in the migration SQL, whichever the existing `AlertChannelMigrationTests` pattern supports).
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter "AlertTypesTests|SenderAlertSettingsTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the properties, the two `AlertTypeInfo` entries (with display names and one-line descriptions in the wording of the existing ones), and `HasDefaultValue` for each new column. Generate the migration with `dotnet ef migrations add AddSenderAlertSettings --project src/DotMarc` and read it: only the five columns on the settings table.
- [ ] **Step 4: Run** the tests, plus `--filter "OpenApi|Alert"`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the sender alert types and their settings"`

---

### Task 2: Working out what to alert on

**Files:**
- Create: `src/DotMarc/Reporting/Senders/SenderAlertEvaluator.cs`
- Modify: `src/DotMarc/Reporting/Senders/SenderDecisionPresentation.cs` (make `PolicyLine` public so alert text and fix text share it)
- Test: `test/DotMarc.Tests/Reporting/SenderAlertEvaluatorTests.cs`

**Interfaces:**
- Consumes: `SenderIdentification.Identify`, `SenderDecisionResolver.Resolve`, `SenderDecision`, `DomainStatistics.IsPassing`.
- Produces, in namespace `DotMarc.Reporting`:
  - `public sealed record SenderAlertSettings(int NewSenderMinVolume, int UnauthorisedDeliveredMinVolume)`
  - `public sealed record SenderAlertInputs(int DomainId, IReadOnlyCollection<int> GroupIds, IReadOnlyList<Report> Reports, IReadOnlyList<SenderDecision> Decisions, IReadOnlyDictionary<string, string?> OwnerByIp, IReadOnlySet<string> NewIps, SenderAlertSettings Settings)`
  - `public sealed record SenderAlertFinding(string Sender, long Messages)`
  - `public sealed record SenderAlertResult(IReadOnlyList<SenderAlertFinding> NewUnreviewed, IReadOnlyList<SenderAlertFinding> UnauthorisedDelivered)`
  - `public static class SenderAlertEvaluator` with `SenderAlertResult Evaluate(SenderAlertInputs inputs)` and `IReadOnlySet<string> NewSourceCandidates(IReadOnlyList<Report> reports, DateTimeOffset newSinceUtc)`, which returns the IPs whose records in the window are all on or after `newSinceUtc` and so might be new. The caller confirms they have no history before the window.

- [ ] **Step 1: Write the failing tests** (build `Report` and `ReportRecord` objects directly, with auth details where a service must be recognised):
  - New and unreviewed: `ANewUnreviewedService_ReachingTheMinimumVolume_IsFound`, `ANewSenderBelowTheMinimumVolume_IsNot`, `AServiceWithADecisionAtAnyLevel_IsNotFound` (theory: domain, group, baseline, and Under review), `AServiceThatIsNotNew_IsNotFound`, `AServiceWhereOnlySomeIpsAreNew_IsNotFound`, `AnUnrecognisedNewIp_IsJudgedOnItsOwn_ByItsIpDecision`, `AnAlignedMicrosoftSenderFoundByOwner_IsNamedAsTheServiceNotAnIp`.
  - Unauthorised delivered: `AnUnauthorisedService_WithDeliveredMailAtOrAboveTheMinimum_IsFound`, `DeliveredMeansDispositionNone_WhetherItPassesOrFails`, `BlockedMail_IsNotCounted`, `BelowTheMinimum_IsNot`, `AnAuthorisedOrUnreviewedService_IsNeverFound`, `AnUnauthorisedIpDecision_OnAnUnrecognisedSource_IsFound`.
  - `NewSourceCandidates_ReturnsOnlyIpsWhoseWholeWindowIsRecent`.
  - Findings are ordered by messages, busiest first.
- [ ] **Step 2: Run** `--filter SenderAlertEvaluatorTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Identify the domain's senders once with `SenderIdentification.Identify`. For each service row, resolve its service-level decision (IP empty) and judge new by all of its `SourceIps` being in `NewIps`. For each unrecognised IP, resolve with the IP. The sender label is the service name, or the IP. Delivered messages come from the records of each decided-unauthorised sender with disposition None.
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Work out which senders to alert on"`

---

### Task 3: Raising and resolving the alerts

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs` (the monitored domains query and the per-domain loop; a new private method), `src/DotMarc/IpEnrichment/IpInfoService.cs` (one small shared owner lookup)
- Test: `test/DotMarc.Tests/Notifications/SenderAlertingTests.cs` (Postgres, modelled on `AlertingServiceTests`)

**Interfaces:**
- Consumes: `SenderAlertEvaluator`, `SenderDecisionService`, `SenderIdentification`, `AlertTypes`, `EnsureAlertAsync(db, settings, domainName, alertType, severity, title, message, cancellationToken)` and `ResolveAlertAsync(domainName, alertType, cancellationToken)`, the existing `IpInfoService.GetCachedAsync` and `GetCachedRangesAsync`.
- Produces: `IpInfoService.OwnerByIp(IReadOnlyDictionary<string, IpInfo> cached, IReadOnlyList<IpRange> ranges, IEnumerable<string> ips, DateTimeOffset nowUtc)` returning `Dictionary<string, string?>` with the same rule the domain page and the API use: the IP's own result while current, else a cached range containing it, else null.

- [ ] **Step 1: Write the failing tests** (a domain with reports, decisions and settings seeded, then `CheckPinnedDomainsAsync`, asserting on `AlertEvents`):
  - `ANewUnreviewedSender_RaisesOneAlertPerDomain_NamingTheSenders_AndWhatToDo`
  - `AnInstallWithOnlyOldSenders_RaisesNoNewSenderAlert` (first seen long ago)
  - `ASourceReappearingAfterAQuietSpell_IsNotNew` (an old report outside the window and a recent one)
  - `ADecisionResolvesTheNewSenderAlert_OnTheNextCycle` (at domain, group and baseline level, and Under review)
  - `AnUnauthorisedSendersDeliveredMail_RaisesAnAlert_WithThePolicyLine`
  - `TheDeliveredAlert_ResolvesWhenTheVolumeDropsBelowTheThreshold`
  - `TurningEachAlertOff_ResolvesAnOpenOne` (both switches)
  - `ANullRoutedDomain_GetsNeitherAlert`
  - `TheMinimumVolumesAndTheNewDays_AreReadFromSettings`
  - `OwnerByIp_UsesTheCurrentIpResult_ThenARange_ThenNull` (a unit test of `IpInfoService.OwnerByIp`)
- [ ] **Step 2: Run** `--filter "SenderAlertingTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Once per cycle, load all `SenderDecisions` (a small table), the cached IP owners and ranges for the IPs in the window, and add `.Include(d => d.Groups)` to the monitored domains query. For each monitored domain that is not null-routed, find `NewSourceCandidates`, then confirm each candidate has no report record before the window start for that domain and IP (one existence query per candidate, so only new-looking sources cost anything), and call the evaluator. Raise or resolve each alert type with `EnsureAlertAsync` or `ResolveAlertAsync`, honouring the two switches. Titles: "New senders to review" and "Mail from an unauthorised sender is being delivered". Messages follow these shapes, with counts using thousands separators: "example.com has 2 new senders that nobody has reviewed: Mailchimp (1,200 messages), 203.0.113.9 (40 messages). Open the domain's Sources tab and mark each one Authorised, Unauthorised or Under review." and "Mail from senders marked unauthorised is being delivered for example.com: Mailchimp (500 messages). {policy line}" using `SenderDecisionPresentation.PolicyLine` for the domain's DMARC policy.
- [ ] **Step 4: Run** the tests, then `--filter "AlertingServiceTests|DnsHealthAlertingTests"` to check nothing else moved. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Alert on new senders and on unauthorised mail being delivered"`

---

### Task 4: Settings screen, docs and a full run

**Files:**
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor` (a "Senders" section near the suspicious reject fields), `website/docs/alerts.mdx`, `website/docs/getting-started.mdx` (one line pointing at the alerts), and the audit change list for saved notification settings wherever the existing fields are enumerated

- [ ] **Step 1: Add the settings section.** Two switches ("Alert on new senders", "Alert when unauthorised mail is delivered") and three numeric fields (minimum messages for a new sender, how many days count as new from 1 to 30, minimum delivered messages), bound to the new settings, saved by the existing Save, with each field's help text saying what it means in plain words. Add the five fields to whatever records changed notification settings in the audit log.
- [ ] **Step 2: Build** `dotnet build src/DotMarc`. Expected: no errors or new warnings.
- [ ] **Step 3: Verify in the browser.** Start a throwaway Postgres container and the app in demo mode, sign in as Demo Admin, open Alert settings and check the section renders, a change saves and survives a reload, an out-of-range days value is refused, and the audit log shows the change. The demo's senders are all older than the new window, so no alert is expected there. Raising the alerts is verified by the Task 3 tests, not by eye. Stop the app and container afterwards.
- [ ] **Step 4: Document** both alert types in `alerts.mdx` (what raises each, what resolves it, the settings and their defaults, that existing senders never alert, that Under review counts as decided, and that a baseline entry silences the new sender alert everywhere). No em dashes.
- [ ] **Step 5: Run the full suite** `dotnet test test/DotMarc.Tests`. Expected: all PASS.
- [ ] **Step 6: Commit** `git commit -m "Configure and document the sender alerts"`
