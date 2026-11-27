# DMARC RFC 9989 alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** dotMARC reads DMARC records the way RFC 9989 does: it names record problems (breaking and informational), counts invalid records, `t=y`, a weak `np` and `pct` below 100 as below target, and the `dmarc` push writes one clean, valid record (raised to the target when there is one).

**Architecture:** One pure analyser in `DotMarc.Dns` turns the TXT values at `_dmarc.<domain>` into a `DmarcRecordAnalysis` (records found, problems, effective tags, validity). One pure repair function turns an analysis plus an optional target into a clean record and a list of plain-words changes. The DMARC check, the target check, the push builder and the domain page all use these two; nothing else parses DMARC policy tags for this purpose. `DmarcPolicyRaise` is removed. `DmarcPolicyTags.Parse` stays for the "policy weakened" alert, which is out of scope.

**Tech Stack:** .NET 10, EF Core + Npgsql, Blazor (MudBlazor), xUnit with the Postgres container fixture, Docusaurus docs.

**Spec:** [DMARC target policy, section "RFC 9989 alignment"](../specs/2026-11-24-dmarc-target-policy-design.md). Builds on [the target policy plan](2026-11-24-dmarc-target-policy.md), all of which is committed.

## Global Constraints

* No em dashes in any prose, comment, UI copy, docs or commit message. Meaningful variable names, including in tests. Warnings fail the build. No hard line wraps in markdown.
* **A valid record with no problems and no target produces exactly today's push plan.** Existing DnsPush tests keep their expectations.
* The push never lowers `p`, `sp`, `np` or `pct` enforcement. Without a target, `pct` and `t` are kept; with a target, `pct` is removed and `t=y` removed.
* A repeated `p`, `sp` or `np` keeps its strictest valid value; any other repeated tag keeps its first value. Non-DMARC TXT values at `_dmarc` are left alone.
* Breaking problems set the DMARC check status to `Misconfigured`; informational problems never change the status or raise an alert.
* Branch `develop`, one commit per task, no push without the user's go-ahead. A migration is added with `dotnet ef migrations add AddDmarcRecordAnalysis --project src/DotMarc` (nullable or defaulted columns only) and `PsaDataMigrationTests` is updated the way Task 2 of the previous plan did (it inserts through the current model into an old schema).
* Browser checks use the playwright-edge tools only. On this machine port 5432 is blocked: run Postgres on 55432 with a connection-string override.

## Review Focus

* A name with a DMARC record and an unrelated TXT value (for example a verification token) is one DMARC record, not two, and the push leaves the token alone (Tasks 1 and 4).
* `v=DMARC1` written in lower case (`v=dmarc1`) is not `DMARC1` exactly: RFC 9989 says the value must match precisely, so the record is ignored (Task 1).
* `t=y` with `p=none` has no effect (RFC 9989 section 4.7) but is still reported as informational, and a domain whose target is `p=none` with `t=y` still meets it (Tasks 1 and 3).
* A record whose only problem is informational (`pct=100`, `rf=afrf`) does not change the check status, so no "DMARC record broken" alert fires on upgrade (Task 2).
* Two DMARC records where one has `p=none` and the other `p=reject` push as one record with `p=reject` (Task 4).

---

### Task 1: Analyse and repair a DMARC record (pure)

**Files:**
- Create: `src/DotMarc/Dns/DmarcRecordAnalysis.cs`, `src/DotMarc/Dns/DmarcRecordRepair.cs`
- Test: `test/DotMarc.Tests/Dns/DmarcRecordAnalysisTests.cs`, `test/DotMarc.Tests/Dns/DmarcRecordRepairTests.cs`

**Interfaces:**
- Produces:
  * `enum DmarcRecordProblemKind { VersionMissingOrNotFirst, MultipleRecords, RepeatedTag, MissingPolicy, InvalidPolicy, InvalidSubdomainPolicy, InvalidNonExistentPolicy, ObsoleteTag, TestingMode }` and `bool IsBreaking(this DmarcRecordProblemKind kind)` (the first seven are breaking; `ObsoleteTag` and `TestingMode` are informational).
  * `record DmarcRecordProblem(DmarcRecordProblemKind Kind, string? Tag, string Description)` with plain-words descriptions, for example "The p tag appears twice. Receivers may ignore the whole record.", "pct is obsolete in RFC 9989 and newer receivers ignore it.", "t=y asks receivers not to apply the policy (testing mode)."
  * `record DmarcRecordAnalysis(IReadOnlyList<string> Records, IReadOnlyList<DmarcRecordProblem> Problems, DmarcPolicyLevel? Policy, DmarcPolicyLevel? SubdomainPolicy, DmarcPolicyLevel? NonExistentPolicy, int? Percent, bool Testing, bool HasValidRua)` with `bool IsValid => !Problems.Any(p => p.Kind.IsBreaking())`, effective getters `EffectiveSubdomainPolicy => SubdomainPolicy ?? Policy`, `EffectiveNonExistentPolicy => NonExistentPolicy ?? SubdomainPolicy ?? Policy`, and `static DmarcRecordAnalysis Analyse(IEnumerable<string> txtValues)`.
  * `record DmarcRecordRepairResult(string Record, IReadOnlyList<string> Changes)` and `static DmarcRecordRepairResult? DmarcRecordRepair.Build(DmarcRecordAnalysis analysis, EffectiveDmarcTarget? target)`: null when there is no DMARC record at all, when the analysis is valid with no informational problem worth changing and no target raise is needed (that is, nothing to change).

Rules (from the spec): only TXT values starting with `v=DMARC1` (tag name compared ignoring case and spaces, value compared exactly, so `v=dmarc1` is a breaking `VersionMissingOrNotFirst`) count as DMARC records; a value starting with `v=` that is not exactly `DMARC1`, or with `v` not first, counts as a DMARC candidate with `VersionMissingOrNotFirst` when no proper record exists. Tag names are compared ignoring case; values for `p`, `sp`, `np`, `t` ignoring case. `Policy` is the strictest valid `p` found (null when none valid); likewise `sp` and `np`. `Percent` is the first valid `pct` (0 to 100). `Testing` is true when any valid `t` is `y`. `HasValidRua` when a `rua` has at least one `mailto:` URI. Repair: `v=DMARC1` first; each tag once, original order kept; strictest valid `p`/`sp`/`np`, first value for other repeated tags; missing or invalid `p` written as the strictest valid `p` found, else `none`; invalid `sp`/`np` dropped; `rf`/`ri` dropped; with no target `pct` and `t` kept; with a target `p`, `sp` (written only when it differs from `p`) and `np` (only when present or weaker than the target `sp`) raised, never lowered, `pct` removed, `t=y` removed. Multiple records are combined by the same rules. Every change gets one line in `Changes`.

- [ ] **Step 1: Write failing analysis tests:** a clean record is valid with no problems; `v=dmarc1; p=reject` is breaking (version); `p=reject; v=DMARC1` is breaking (version not first); two DMARC values give `MultipleRecords`, a DMARC value plus `google-site-verification=abc` is one record; `p=none; p=reject` gives `RepeatedTag` with `Policy` Reject; missing `p` gives `MissingPolicy`; `p=rejct` gives `InvalidPolicy`; `sp=bogus` and `np=bogus` give their invalid kinds; `pct=50`, `rf=afrf`, `ri=86400` give `ObsoleteTag` (informational, record still valid); `t=y` gives `TestingMode` (informational); effective `sp` and `np` inheritance; upper-case tag names parse.
- [ ] **Step 2: Write failing repair tests** with exact expected strings: no target and a clean record returns null; `v=DMARC1; p=none; p=reject; rua=mailto:a@x` gives `v=DMARC1; p=reject; rua=mailto:a@x`; two records `v=DMARC1; p=none; rua=mailto:a@x` and `v=DMARC1; p=reject` give `v=DMARC1; p=reject; rua=mailto:a@x`; `v=DMARC1; rua=mailto:a@x` with no target gives `v=DMARC1; p=none; rua=mailto:a@x`; `v=DMARC1; p=quarantine; pct=50; rf=afrf` with no target keeps `pct=50`, drops `rf`; the same with target reject gives `v=DMARC1; p=reject`; `v=DMARC1; p=reject; t=y` with target reject gives `v=DMARC1; p=reject`; `v=DMARC1; p=reject; np=none` with target `p=reject` gives `v=DMARC1; p=reject; np=reject`; `v=DMARC1; p=reject` with target quarantine returns null (already stricter); every result lists its changes.
- [ ] **Step 3: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DmarcRecordAnalysis|FullyQualifiedName~DmarcRecordRepair"`. Expected: FAIL (types missing).
- [ ] **Step 4: Implement** the two files.
- [ ] **Step 5: Run** the same filter. Expected: PASS.
- [ ] **Step 6: Commit** `Analyse and repair DMARC records the way RFC 9989 reads them`.

### Task 2: The DMARC check stores the analysis

**Files:**
- Modify: `src/DotMarc/Dns/DmarcDnsChecker.cs` (collect every TXT value at `_dmarc.<domain>`, not only the first; analyse), `src/DotMarc/Dns/DmarcCheckResult.cs`, `src/DotMarc/Data/Domain.cs`, `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Ingestion/PollingService.cs` (`RunSingleDmarcCheckAsync`), migration `AddDmarcRecordAnalysis`, `test/DotMarc.Tests/...PsaDataMigrationTests.cs`
- Test: `test/DotMarc.Tests/Dns/DmarcDnsCheckerTests.cs` (follow its existing fake HTTP setup), `test/DotMarc.Tests/Ingestion` where `RunSingleDmarcCheckAsync` is tested

**Interfaces:**
- Consumes: `DmarcRecordAnalysis` (Task 1).
- Produces: `DmarcCheckResult` gains `DmarcRecordAnalysis? Analysis`. `Domain` gains `DmarcPolicyLevel? DmarcNonExistentSubdomainPolicy`, `bool DmarcTesting`, `List<string> DmarcRecords` (the raw DMARC values, like `DkimSelectors`), `List<string> DmarcProblems` (problem kinds as stored names, plus tag where relevant, for example `RepeatedTag:p`). `RunSingleDmarcCheckAsync` writes them.

- [ ] **Step 1: Write failing checker tests:** a breaking problem (repeated `p`, two DMARC records, missing `p`, version not first) returns `Misconfigured` with the problem's description in the detail and the analysis attached; an informational problem only (`pct=100`, `t=y`, `rf=afrf`) keeps whatever status the rua checks give (for example `Ok`) and attaches the analysis; a DMARC record plus a verification TXT value is treated as one record; a missing record is `MissingOwnRecord` as today; every existing checker test passes unchanged.
- [ ] **Step 2: Write a failing test** that `RunSingleDmarcCheckAsync` stores `DmarcNonExistentSubdomainPolicy`, `DmarcTesting`, `DmarcRecords` and `DmarcProblems`, and that the existing `DmarcPolicy`, `DmarcSubdomainPolicy`, `DmarcPercent` come from the analysis (strictest valid values).
- [ ] **Step 3: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DmarcDnsChecker|FullyQualifiedName~DotMarc.Tests.Ingestion"`. Expected: new tests FAIL.
- [ ] **Step 4: Implement**, add the migration, update `PsaDataMigrationTests`.
- [ ] **Step 5: Run** the filter plus `FullyQualifiedName~PsaDataMigrationTests|FullyQualifiedName~DotMarc.Tests.Notifications`. Expected: PASS.
- [ ] **Step 6: Commit** `Name DMARC record problems in the DMARC check`.

### Task 3: Target outcome follows RFC 9989

**Files:**
- Modify: `src/DotMarc/Baselines/DmarcTargetCheck.cs`, `src/DotMarc/Baselines/DmarcTargetText.cs`, `src/DotMarc/Notifications/DnsHealthAlertEvaluator.cs` (below-target message), `src/DotMarc/Demo/` (one demo domain with `t=y`, one with a repeated tag, so both show)
- Test: `test/DotMarc.Tests/Baselines/DmarcTargetCheckTests.cs`, `test/DotMarc.Tests/Notifications/DnsHealthAlertEvaluatorTests.cs`, demo tests

**Interfaces:**
- Produces: `static IReadOnlyList<string> DmarcTargetCheck.Reasons(Domain domain, EffectiveDmarcTarget target)` giving the plain-words reasons a domain is below target ("p is none, target is reject", "Testing mode (t=y) is on", "np is none, target for subdomains is reject", "pct is 50", "The record is malformed: the p tag appears twice"). `Outcome` uses the same conditions.

- [ ] **Step 1: Write failing outcome tests:** a stored breaking problem gives Below with the problem as a reason, even when `p` meets the target; `DmarcTesting` true gives Below for a quarantine or reject target and Meets for a `p=none` target; a weak `np` gives Below; `np` absent inherits; `pct` 100 meets; existing outcome tests pass.
- [ ] **Step 2: Write a failing evaluator test:** the below-target alert message lists the reasons.
- [ ] **Step 3: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Baselines|FullyQualifiedName~DnsHealthAlertEvaluator|FullyQualifiedName~DotMarc.Tests.Demo"`. Expected: FAIL.
- [ ] **Step 4: Implement** and update the demo data.
- [ ] **Step 5: Run** the filter. Expected: PASS.
- [ ] **Step 6: Commit** `Count invalid records, testing mode and weak np as below target`.

### Task 4: Push writes one clean record

**Files:**
- Modify: `src/DotMarc/DnsPush/DnsRecordLookupResult.cs` (add `IReadOnlyList<string> TxtValues`, every TXT value at the name), `src/DotMarc/DnsPush/DnsRecordLookupParsing.cs`, `src/DotMarc/DnsPush/DmarcChangeBuilder.cs`, `src/DotMarc/DnsPush/IDnsChangeBuilder.cs` (`DnsChangePlan` gains `IReadOnlyList<string> Notes`, default empty), `src/DotMarc/DnsPush/DnsPushReview.cs` (carries the notes), the review UI component that renders `DnsPushReview` (find it), and the bulk review if it renders notes separately
- Delete: `src/DotMarc/Baselines/DmarcPolicyRaise.cs` and its tests
- Test: `test/DotMarc.Tests/DnsPush/DnsChangeBuilderTests.cs`, `DnsRecordLookupParsingTests` (or where parsing is tested), `DnsPushReviewTests`

**Interfaces:**
- Consumes: `DmarcRecordAnalysis.Analyse`, `DmarcRecordRepair.Build` (Task 1), the target reader.
- Produces: the builder plans as follows. No DMARC record: create as today, using `DmarcRecordRepair` only to apply a target (no target gives today's `v=DMARC1; p=none; {rua}`). One DMARC record: repair (if any), then `DmarcRuaMerge.TryMerge` on the result; `Merge` change replacing the existing value. More than one DMARC record: one `ReplaceTxtValues` change with `ValuesToRemove` = every DMARC value and `DesiredValue` = the repaired, rua-merged record; non-DMARC values untouched. Notes: when the repair changed anything, the first note is "Repairs a malformed DMARC record" (only when a breaking problem existed) followed by each change line.

- [ ] **Step 1: Write failing tests:** a clean record and no target gives exactly today's plan (existing tests unchanged); repeated `p` gives one Merge with the repaired value and notes; two DMARC records plus a verification value give one `ReplaceTxtValues` removing both DMARC values only, with `p=reject` kept from the stricter; a target raise on a clean record lists its changes as notes but no "Repairs" note; the review exposes the notes; the CNAME-delegated path still uses the target or `p=none`.
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.DnsPush|FullyQualifiedName~DotMarc.Tests.Baselines"`. Expected: FAIL.
- [ ] **Step 3: Implement**; delete `DmarcPolicyRaise` and its tests; show notes on the single push review and the bulk review.
- [ ] **Step 4: Run** the filter plus `FullyQualifiedName~ProgramDiValidation|FullyQualifiedName~OpenApiDocument`. Expected: PASS.
- [ ] **Step 5: Commit** `Push one clean DMARC record and say what it repairs`.

### Task 5: Domain page, docs and release notes

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor` (DMARC card: list each stored problem, breaking ones as errors and informational ones as info; show the suggested clean record from `DmarcRecordRepair.Build(Analyse(domain.DmarcRecords), effective target)` with a copy button, only when there is one; the target card shows `DmarcTargetCheck.Reasons`), `website/docs/baselines.mdx`, `website/docs/dmarc-and-mta-sts.mdx` (record problems section citing RFC 9989), `website/docs/dns-provider-push.mdx`, `website/docs/alerts.mdx`, the roadmap release notes draft
- Test: `test/DotMarc.Tests/Components/DocsLinkTests.cs` (if a docs link is added), demo tests

- [ ] **Step 1: Implement** the domain page changes. Copy: a heading "Record problems", each problem's description, and "Suggested record" with a Copy button and the line "dotMARC's DNS push writes this record."
- [ ] **Step 2: Docs.** Explain RFC 9989 in plain words: the record problems and what receivers do with each, `t=y`, `np`, that `pct`, `rf` and `ri` are obsolete, how a repeated tag is repaired (strictest policy kept), and that a push collapses several DMARC records into one. Update the "meeting a target" rules in `baselines.mdx`. Release notes: domains with genuinely broken DMARC records (repeated tags, several records, missing or invalid `p`) may now raise "DMARC record broken" after upgrading; `pct`, `rf`, `ri` and `t=y` are flagged but do not alert.
- [ ] **Step 3: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DocsLink|FullyQualifiedName~DotMarc.Tests.Components|FullyQualifiedName~DotMarc.Tests.Demo"` and `dotnet build`. Expected: PASS, zero warnings.
- [ ] **Step 4: Browser check** in demo mode: the repeated-tag demo domain shows its problem and a suggested record; the `t=y` domain shows testing mode as informational and as the reason it is below target.
- [ ] **Step 5: Run the full suite** `dotnet test test/DotMarc.Tests --blame-hang --blame-hang-timeout 10m`. Expected: all pass.
- [ ] **Step 6: Commit** `Show DMARC record problems and a suggested record`.
