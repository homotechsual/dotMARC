# Bulk DNS push, slice A: the engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tested engine that takes a batch of domains on one DNS provider and one record target, lets the plan for each be reviewed, then pushes them all with one sign-in, recording a result and an audit entry per domain. No UI.

**Architecture:** Split each provider's one-shot `ExchangeAndPushAsync` into an exchange (once) and a push (per domain). Extract the callback's zone detection and plan building into a planner shared by the single push and the batch. Store a batch and its per-domain rows in the database, carrying only the batch id in the OAuth `state`. After the single token exchange, a background worker pushes each domain, re-planning at push time and skipping any domain whose plan no longer matches what was reviewed.

**Tech Stack:** .NET 10, EF Core + Npgsql, ASP.NET minimal APIs, `BackgroundService` with a `Channel`, xUnit with the Postgres container fixture and `FakeHttpMessageHandler`.

**Spec:** [bulk DNS push](../specs/2026-11-20-bulk-dns-push-design.md) (slice A of [the 0.14.0 roadmap](2026-11-20-v0-14-0-roadmap.md)).

## Global Constraints

* No em dashes in any prose, comment or commit message. Meaningful variable names, including in tests. Warnings fail the build. No hard line wraps in markdown.
* **The single-domain push behaves exactly as before:** the same popup result flags, the same audit entries, the same provider calls. Every existing test in `test/DotMarc.Tests/DnsPush` passes unchanged.
* **The provider session (access token) is held in memory only.** It is never stored, never logged, never put in an audit entry or an exception message, and is dropped when the batch ends.
* Limits (constants, one place each): at most **50** domains per batch, one provider and one target per batch, a batch can be signed in to for **30 minutes** after it is created, finished batches are kept **7 days**, a pause of **250 ms** between domains (tests pass zero).
* Targets allowed in a batch: `dmarc`, `tlsrpt`, `mta-sts`. `spf`, `dkim` and `dmarc-auth` are refused.
* Branch `develop`, one commit per task, no push without the user's go-ahead. Rules that touch a migration add it with the project's usual `dotnet ef migrations add`, and `PsaDataMigrationTests` is re-run, because it exercises old migrations.
* Alerting's lesson applies: test contexts that load more than one collection use a context configured to throw on `MultipleCollectionIncludeWarning`, and loaders use `AsSplitQuery()`.

## Review Focus

* A domain whose plan changed since review (including one that now has nothing to change) is skipped and reported, never pushed (Task 6).
* One domain failing (zone not found, a provider error, a replace that failed after the delete) does not stop the others, and each gets its own audit entry (Task 6).
* A domain whose DNS has moved to another provider since planning reports zone not found and is not pushed (Task 6).
* A batch id is single use, expires, and cannot be used by another person, for another provider, or through the single-domain endpoint (Task 5).
* A process stopping mid-batch leaves nothing Running for ever (Task 6).
* The single-domain flags and audit are unchanged after the provider split and the planner extraction (Tasks 2 and 3).

---

### Task 1: Plan fingerprint

**Files:**
- Create: `src/DotMarc/DnsPush/DnsPlanFingerprint.cs`
- Test: `test/DotMarc.Tests/DnsPush/DnsPlanFingerprintTests.cs`

**Interfaces:**
- Produces: `DnsPlanFingerprint.Of(IReadOnlyList<DnsRecordChange> changes) : string`, a stable hash over each change's `Kind`, `RecordType`, `Name`, `DesiredValue`, `ExistingValue`, `ZoneName`, `ExistingRecordType` and `ValuesToRemove` (order preserved), as a lowercase hex SHA-256.

- [ ] **Step 1: Write failing tests (pure).** Same changes give the same fingerprint, including two separately built lists. A different `DesiredValue`, `ExistingValue`, `Kind`, `ZoneName` or `ValuesToRemove`, an extra change, or a different order each give a different one. An empty list has a fixed, non-null fingerprint. `ValuesToRemove` null and empty behave the same.
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter DnsPlanFingerprintTests`. Expected: FAIL (type missing).
- [ ] **Step 3: Implement** `Of` with a length-prefixed serialisation of the fields (so `"ab","c"` and `"a","bc"` differ), then SHA-256.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `Fingerprint a DNS change plan`.

### Task 2: Split the provider call into exchange and push

**Files:**
- Modify: `src/DotMarc/DnsPush/IDnsPushProvider.cs`, `CloudflareDnsPushProvider.cs`, `AzureDnsPushProvider.cs`, `GoogleCloudDnsPushProvider.cs`
- Create: `src/DotMarc/DnsPush/DnsPushProviderExtensions.cs`
- Test: add to `CloudflareDnsPushProviderTests.cs`, `AzureDnsPushProviderTests.cs`, `GoogleCloudDnsPushProviderTests.cs`; existing tests unchanged

**Interfaces:**
- Produces:
  * `IDnsPushSession` (marker interface, no members). Each provider has its own private implementation holding what it needs (the access token for Cloudflare and Google, the `ArmClient` for Azure). It is never serialised.
  * `DnsPushSessionResult(IDnsPushSession? Session, DnsPushResult? Failure)`.
  * On `IDnsPushProvider`: `Task<DnsPushSessionResult> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)` and `Task<DnsPushResult> PushAsync(IDnsPushSession session, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)`.
  * `ExchangeAndPushAsync(this IDnsPushProvider provider, string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)` as an extension method with the same signature and behaviour as today (exchange, return its failure if it failed, otherwise push), so existing callers and tests compile unchanged. It is removed from the interface and the three classes.

- [ ] **Step 1: Write failing tests** per provider, using the existing fake handler pattern: after one `ExchangeAsync`, two `PushAsync` calls for two different zones both succeed using the one token (the handler sees exactly one token request); a `PushAsync` that returns zone not found for one zone does not prevent a later `PushAsync` for another; a failed exchange returns `Failure` and a null `Session`; the not-configured case still returns the "not configured" message from `ExchangeAsync`.
- [ ] **Step 2: Run** the three provider test classes. Expected: the new tests FAIL (members missing).
- [ ] **Step 3: Implement** the interface changes, the extension, and the three providers: move the existing "configured" check and token exchange into `ExchangeAsync`, and the existing per-change loop (stop at the first non-`Pushed`) into `PushAsync`. Google keeps its outer `HttpRequestException` and `TaskCanceledException` catch in both methods.
- [ ] **Step 4: Run every test in `test/DotMarc.Tests/DnsPush`.** Expected: PASS, including every existing provider test.
- [ ] **Step 5: Commit** `Split the DNS push provider call into exchange and push`.

### Task 3: Share the callback's planning

**Files:**
- Create: `src/DotMarc/DnsPush/DnsPushPlanner.cs`
- Modify: `src/DotMarc/Program.cs` (the callback's zone detection and plan building, lines about 750 to 783)
- Test: `test/DotMarc.Tests/DnsPush/DnsPushPlannerTests.cs`

**Interfaces:**
- Produces: `DnsPushPlanResult(DnsChangePlan? Plan, string? Flag)` and `DnsPushPlanner(IDnsProviderDetector detector)` with `Task<DnsPushPlanResult> PlanAsync(Domain domain, IDnsChangeBuilder builder, string provider, string? payload, CancellationToken cancellationToken)`. Behaviour moved unchanged from the callback: for a builder that writes to the domain's zone, detect the zone, return flag `error` when detection throws `HttpRequestException`, flag `zone-not-found` when the detected provider key is not `provider`, otherwise build with the detected zone; return the plan's own refusal as the flag.

- [ ] **Step 1: Write failing tests** with a fake detector and fake builders: a matching provider builds the plan with the detected zone name; a different provider gives `zone-not-found` and never calls the builder; a detector throwing `HttpRequestException` gives `error`; a builder that refuses gives its flag; a builder with `WritesToDomainZone` false skips detection and passes a null zone.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the planner, register it as a singleton, and make the callback call it, returning `DnsPushPopupResult.Close(flag, ...)` exactly as before for a flag.
- [ ] **Step 4: Run the DnsPush and Api test groups.** Expected: PASS.
- [ ] **Step 5: Commit** `Share DNS push planning between the callback and batches`.

### Task 4: The batch records and the review

**Files:**
- Create: `src/DotMarc/DnsPush/DnsPushBatch.cs` (entities and enums), `src/DotMarc/DnsPush/DnsPushBatchService.cs`, a migration `AddDnsPushBatches`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Program.cs` (register the service)
- Test: `test/DotMarc.Tests/DnsPush/DnsPushBatchServiceTests.cs` (Postgres fixture)

**Interfaces:**
- Produces:
  * `DnsPushBatch` (`Guid Id`, `string StartedBy`, `string Provider`, `string Target`, `DnsPushBatchStatus Status`, `CreatedUtc`, `ExpiresUtc`, `StartedUtc?`, `CompletedUtc?`, `List<DnsPushBatchDomain> Domains`) and `DnsPushBatchDomain` (`int Id`, `Guid BatchId`, `int DomainId`, `string DomainName`, `int Position`, `bool Included`, `DnsPushBatchDomainStatus Status`, `string? PlanFingerprint`, `string? Detail`, `DateTimeOffset? FinishedUtc`).
  * `DnsPushBatchStatus { Planned, Running, Completed, Interrupted, Expired }`.
  * `DnsPushBatchDomainStatus { Planned, NothingToChange, Refused, Pushed, ChangedSinceReview, ZoneNotFound, Failed, FailedAfterChange, NotAttempted }` (`FailedAfterChange` is a replace that failed after the delete, or a duplicate SPF left behind: the outcomes that may have changed the zone).
  * `DnsPushBatchService.CreateAsync(DotMarcDbContext context, AuditActor actor, string provider, string target, IReadOnlyList<int> domainIds, CancellationToken cancellationToken) : Task<DnsPushBatch>`: refuses (throws `DnsPushBatchException` with a message) an empty or over-50 list, a target other than `dmarc`, `tlsrpt` or `mta-sts`, and an unknown provider. Whether the person holds the target's policy is checked by the endpoint that calls this (Task 5), not here. For each domain id it plans through `DnsPushPlanner`: a plan with additions is `Planned` with its fingerprint, a plan with none is `NothingToChange`, a planner flag is `Refused` with the flag in `Detail` (which covers a domain whose DNS is on another provider: `zone-not-found`), and an id that matches no domain is `Refused` with detail `not-found` and domain name left blank. Rows are `Included` only when `Planned`. Creating a batch records no audit entry, since nothing has changed yet: only starting the run is audited (Task 7).
  * `DnsPushBatchService.SetIncludedAsync(DotMarcDbContext context, AuditActor actor, Guid batchId, IReadOnlyList<int> includedDomainIds, CancellationToken cancellationToken)` for a still-`Planned`, unexpired batch the actor started; only `Planned` rows can be included.

- [ ] **Step 1: Write failing tests** with a fake planner or fake builders: 3 domains give 3 rows with the right statuses and fingerprints, `Included` only for `Planned`; 51 domains, an empty list, `spf` and `dkim` and `dmarc-auth` targets and an unknown provider are each refused; a domain id that doesn't exist is left out and named in the exception or skipped with `Refused` (pick skipped with `Refused` and detail `not-found`); `SetIncludedAsync` by the person who started it works, by another person is refused, after expiry is refused, and including a `Refused` row is refused. `Position` follows the input order.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** entities, `DbSet`s and model configuration (the `Id` of the batch is the primary key, the domain rows cascade, an index on `(BatchId, Position)` and on `ExpiresUtc`), the migration, the service and the constants.
- [ ] **Step 4: Run the new tests and `PsaDataMigrationTests`.** Expected: PASS.
- [ ] **Step 5: Commit** `Store a batch of DNS pushes and review its plans`.

### Task 5: Carry a batch through the OAuth sign-in

**Files:**
- Modify: `src/DotMarc/DnsPush/DnsPushState.cs`, `DnsPushStateProtector.cs`, `src/DotMarc/Program.cs` (a new `/dns-push/{provider}/start-batch` endpoint and a batch branch in the callback)
- Test: `DnsPushStateProtectorTests.cs` (extend), `test/DotMarc.Tests/DnsPush/DnsPushBatchStartTests.cs` (endpoint tests through the existing web test host, if one covers the single start; otherwise unit tests on a small `DnsPushBatchGate` extracted for the checks)

**Interfaces:**
- `DnsPushState` gains `Guid? BatchId = null`. A batch state has `DomainId` 0 and a `PushTarget` of the batch's target. `DnsPushStateProtector.Protect` gains an optional `batchId` parameter, and `Unprotect` returns it.
- `GET /dns-push/{provider}/start-batch?batchId=`: same guard (`DnsPushStartGuard`), same configured-provider check, the batch's target policy (`IDnsChangeBuilder.RequiredPolicy`), and the batch must exist, be `Planned`, unexpired, started by this person, for this provider, with at least one `Included` domain. Otherwise `400`, `403` or `404` as the single start does for the equivalent case.
- Callback: a state with `BatchId` takes the batch branch. It re-checks the same things, marks the batch `Running` in one atomic update that fails if it was already used (single use), then hands the code and verifier to the runner (Task 6) and closes the popup with the flag `batch-started` and the batch id as the push id. A state without a `BatchId` behaves exactly as today. The single-domain `/start` refuses a request that tries to pass a batch.

- [ ] **Step 1: Write failing tests.** Protector: a batch id round-trips, expires with the state, and a tampered value is rejected. Start: no batch, another person's batch, a wrong provider, an expired batch, a batch with nothing included, an unconfigured provider and a missing policy are each refused; a good one redirects to the provider's authorization URL whose `state` unprotects to the batch id. Callback gate: a second use of the same batch is refused; a batch state does not run the single-domain path, and a single-domain state ignores any batch.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the state change, the endpoint, the callback branch and the single-use claim (an `UPDATE ... WHERE Status = Planned` returning the row count).
- [ ] **Step 4: Run the DnsPush and Api test groups.** Expected: PASS, including the existing single-push tests.
- [ ] **Step 5: Commit** `Start a batch of DNS pushes through one provider sign-in`.

### Task 6: The runner

**Files:**
- Create: `src/DotMarc/DnsPush/DnsPushBatchRunner.cs` (the per-batch logic), `src/DotMarc/DnsPush/DnsPushBatchWorker.cs` (the `BackgroundService` and its `Channel`)
- Modify: `src/DotMarc/Program.cs` (register both; the callback's hand-off enqueues the work)
- Test: `test/DotMarc.Tests/DnsPush/DnsPushBatchRunnerTests.cs`, `DnsPushBatchWorkerTests.cs` (Postgres fixture, a scripted `FakeDnsPushProvider` in `test/DotMarc.Tests/Internal`)

**Interfaces:**
- `DnsPushBatchRunner.RunAsync(Guid batchId, IDnsPushSession session, IDnsPushProvider provider, AuditActor actor, TimeSpan pauseBetweenDomains, CancellationToken cancellationToken)`: for each `Included` row in position order: load the domain, plan again through `DnsPushPlanner`; a flag from the planner sets `ZoneNotFound` (flag `zone-not-found`) or `Failed` (anything else) with the flag in `Detail`; a plan whose fingerprint differs from `PlanFingerprint` sets `ChangedSinceReview` without pushing; an unchanged plan is pushed with `provider.PushAsync(session, plan.Changes, ...)`; the outcome maps to `Pushed`, `ZoneNotFound`, `Failed`, or `FailedAfterChange` (`ReplaceFailedAfterDelete` and `LeftDuplicateRecord`); each attempted push records `DnsPushAudit.CreateEntry(...)` exactly as the single path does. One domain's outcome never stops the loop. Rows not included stay as they were. At the end the batch is `Completed` with `CompletedUtc`.
- `DnsPushBatchWorker`: a hosted service reading `(batchId, session, providerKey, actor)` items from an unbounded `Channel` and running them one at a time. It drops the session reference when each item ends. On startup it marks any batch left `Running` as `Interrupted` and its not-yet-finished included rows `NotAttempted`. It deletes batches finished more than 7 days ago, and `Planned` ones past their expiry (as `Expired`, then deleted with the rest).

- [ ] **Step 1: Write failing tests** with the scripted fake provider (it returns a chosen `DnsPushResult` per zone and counts calls): all-good batch pushes every included domain with one `PushAsync` each and the batch `Completed`; a domain whose plan changed (the fake builder returns a different value the second time) is `ChangedSinceReview` and `PushAsync` is not called for it; a domain with nothing to change at push time is `ChangedSinceReview` too (the reviewed plan had additions); a zone-not-found result, a provider error and a `ReplaceFailedAfterDelete` result each set their status and the following domains are still pushed; the planner reporting `zone-not-found` (DNS moved) does not call `PushAsync`; each attempted push has exactly one audit entry and a skipped one has none; a domain not `Included` is untouched; the pause is honoured (a stub delay records it); cancellation stops the loop and marks the rest `NotAttempted`. Worker: a batch left `Running` at startup becomes `Interrupted` with unfinished rows `NotAttempted`; items run one at a time in order; expired and old batches are removed; the session reference is cleared after the run.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the runner, the worker and the hand-off.
- [ ] **Step 4: Run the new tests and the whole DnsPush group.** Expected: PASS.
- [ ] **Step 5: Commit** `Push a reviewed batch of DNS changes, one domain at a time`.

### Task 7: Audit the batch and prove the whole flow

**Files:**
- Modify: `src/DotMarc/Audit/AuditActions.cs`, `DnsPushBatchService.cs` or the callback hand-off (where the run is started)
- Test: `test/DotMarc.Tests/DnsPush/DnsPushBatchFlowTests.cs`; the existing `AuditCoverageTests` and audit action list tests

**Interfaces:**
- New action `DnsPushBatchStarted = "dns.push_batch_started"` with the display text "DNS push batch started", recorded (as an `AuditEntryKind.Change` with a null `AuditTarget`, since it is about no single entity) when the batch moves from `Planned` to `Running`. The summary reads "Started a DNS push of N domains to cloudflare (dmarc)", with N the included count and the provider key and target filled in. It carries no field changes. The per-domain entries come from the runner (Task 6).

- [ ] **Step 1: Write failing tests.** Starting a run records one batch entry with the counts and nothing secret (no code, verifier, token or state); the end-to-end flow with fakes (create, include a subset, claim, run) leaves the right rows and exactly one audit entry per attempted domain plus the batch one; `AuditCoverageTests` stays green.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the action and the entry.
- [ ] **Step 4: Run the full suite** to a file and read the tail. Expected: all pass, 0 failed.
- [ ] **Step 5: Commit** `Audit a DNS push batch`.
