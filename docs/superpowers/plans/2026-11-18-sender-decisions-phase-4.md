# Sender Decisions Phase 4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface sender decisions in three more places: a sentence in the client report PDF, a read-only `decision` field in the sources API, and an "N senders to review" count on the domain Overview.

**Architecture:** One new pure function, `SenderDecisionSummary.Summarise`, turns a domain's reports plus its decisions into the counts all three places need (unreviewed senders, unauthorised mail blocked, unauthorised mail delivered). It resolves decisions with the existing `SenderDecisionResolver` and attributes senders with the existing `SenderIdentification`, exactly as `SenderAlertEvaluator` does, so the table, alerts, report, API and Overview cannot disagree. The API field resolves each page IP with the resolver directly.

**Tech Stack:** .NET 10, EF Core + Npgsql, Blazor Server + MudBlazor 9, MigraDoc (report PDF), xUnit with the Postgres fixture.

**Spec:** `docs/superpowers/specs/2026-11-18-sender-decisions-design.md` (sections "Client report" and "Phasing", item 4). Phases 1 to 3 are built; this plan reuses `SenderDecisionResolver`, `SenderDecisionService.LoadForDomainAsync`, `SenderIdentification` and `SenderDecisionStatusLabels`.

## Global Constraints

- No em dashes in any copy or docs.
- Wording is plain, no jargon: "senders you haven't authorised", not "unauthorised principals".
- Nothing appears for a domain with no unauthorised mail (report) or no unreviewed senders (Overview).
- The resolution rule lives only in `SenderDecisionResolver`; this phase adds no second copy of it.
- Client portal users never see decisions. The portal pages and `PortalDomainDetail` are not touched.
- The API field is read-only and needs only the existing `DomainsView` permission (the spec: anyone who can see the domain can see decisions).
- No migration, no new permission, no new setting.
- Commit message style: short imperative sentence, no prefix, as in `git log`.

## Review Focus

- **Baseline entry for a service the domain never used:** report and Overview show nothing for it; only senders present in the period's reports count.
- **Unauthorised sender whose mail all passes DMARC:** counted as delivered; the report must not claim "the policy doesn't block them", because passing mail is delivered by design.
- **Authorised and Under review senders:** never counted as blocked or delivered unauthorised mail.
- **Unauthorised sender with mixed dispositions:** blocked and delivered counted separately per message count, not per IP.
- **Group-limited API caller / domain in several groups:** group decisions resolve with the domain's own group ids (most cautious wins), same as the Sources tab.
- **Source IP with no matched service in the API:** resolves by IP decision, then baseline cannot apply (no service), so `Unreviewed` unless the IP has its own decision.
- **Singular and plural:** "1 sender to review" and "1 message" read correctly.

---

### Task 1: The summary function

**Files:**
- Create: `src/DotMarc/Reporting/Senders/SenderDecisionSummary.cs`
- Test: `test/DotMarc.Tests/Reporting/Senders/SenderDecisionSummaryTests.cs`

**Interfaces:**
- Consumes: `SenderIdentification.Identify(IReadOnlyList<Report>, IReadOnlyDictionary<string,string?>, IReadOnlyCollection<string>)` returning `SenderIdentificationResult(Services, Unrecognised, ServiceByIp)`, where `Services` rows have `Name`, `SourceIps`, `Messages` and `Unrecognised?.SourceIps`; `SenderDecisionResolver.Resolve(decisions, domainId, groupIds, serviceName, ip, groupNames)` returning `ResolvedSenderDecision` with `Status` (`SenderDecisionStatus?`, null = Unreviewed); `DomainStatistics.IsPassing(ReportRecord)`; `DispositionResult`.
- Produces:
  - `public sealed record SenderDecisionSummary(int UnreviewedSenders, long UnauthorisedBlocked, long UnauthorisedDelivered, long UnauthorisedDeliveredFailing)`
  - `public static SenderDecisionSummary SenderDecisionSummaries.Summarise(IReadOnlyList<Report> reports, IReadOnlyDictionary<string, string?> ownerByIp, IReadOnlyList<SenderDecision> decisions, int domainId, IReadOnlyCollection<int> groupIds)` (static class named `SenderDecisionSummaries` in the same file, namespace `DotMarc.Reporting`).

- [ ] **Step 1: Write the failing tests** in `SenderDecisionSummaryTests`, building `Report`/`ReportRecord`/`SenderDecision` objects in memory the way `SenderAlertEvaluatorTests` does. Tests, each with the assertions named:
  - `Summarise_CountsAServiceWithNoDecisionAsOneUnreviewedSender`: two IPs matched to one service, no decisions → `UnreviewedSenders == 1`.
  - `Summarise_CountsAnUnmatchedIpAsItsOwnUnreviewedSender`: one unmatched IP → `UnreviewedSenders == 1`.
  - `Summarise_DoesNotCountAnyDecisionAsUnreviewed`: Authorised, UnderReview and Unauthorised decisions each on its own service (domain scope) → `UnreviewedSenders == 0`.
  - `Summarise_BaselineDecisionClearsTheSenderOnEveryDomain`: baseline Authorised for the service, no domain decision → `UnreviewedSenders == 0`.
  - `Summarise_SplitsUnauthorisedMailIntoBlockedAndDelivered`: Unauthorised service with 30 messages quarantined or rejected, 12 with disposition none and failing, 8 with disposition none and passing → `UnauthorisedBlocked == 30`, `UnauthorisedDelivered == 20`, `UnauthorisedDeliveredFailing == 12`.
  - `Summarise_IgnoresMailFromAuthorisedAndUnderReviewSenders`: both decisions with failing delivered mail → all three unauthorised counts 0.
  - `Summarise_AnIpDecisionOverridesItsService`: service Authorised, one of its IPs Unauthorised with 5 delivered messages → `UnauthorisedDelivered == 5`.
  - `Summarise_ConflictingGroupDecisionsResolveToTheMostCautious`: groups 1 and 2 decide Authorised and Unauthorised → mail counted as unauthorised.
  - `Summarise_ReturnsZerosForNoReports`: empty reports → all zeros.

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SenderDecisionSummaryTests"`
Expected: build error (type not defined), then FAIL.

- [ ] **Step 3: Implement `SenderDecisionSummaries.Summarise`** in `SenderDecisionSummary.cs`. Identify once over all reports; an unreviewed sender is a service row whose service-level resolve (`ip` empty) has null `Status`, plus each unrecognised IP whose IP-level resolve (`serviceName` null) has null `Status` (the same rule as `SenderAlertEvaluator.Evaluate`'s new-sender check, minus the new-ness and volume tests). For mail, resolve per source IP with the IP's service from `ServiceByIp`, keep records of IPs resolving to `Unauthorised`, and sum `MessageCount` by `Disposition` (`None` is delivered, `Quarantine` and `Reject` are blocked); `UnauthorisedDeliveredFailing` is the delivered records where `!DomainStatistics.IsPassing`. Pass an empty group-name dictionary.

- [ ] **Step 4: Run to verify GREEN**

Run: the Step 2 command. Expected: 9 passed.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Reporting/Senders/SenderDecisionSummary.cs test/DotMarc.Tests/Reporting/Senders/SenderDecisionSummaryTests.cs
git commit -m "Count unreviewed senders and unauthorised mail for a domain"
```

---

### Task 2: The client report sentence

**Files:**
- Modify: `src/DotMarc/Reporting/ClientReports/ClientReport.cs` (`ClientReportDomain` gains `SenderDecisionSummary? Senders = null` as the last parameter), `ClientReportCalculator.cs` (`ClientReportInputs` gains `IReadOnlyList<SenderDecision>? Decisions = null` as the last parameter; `Build` fills `Senders`), `ClientReportBuilder.cs`, `ClientReportDocument.cs`
- Test: `test/DotMarc.Tests/Reporting/ClientReports/ClientReportCalculatorTests.cs`, `ClientReportDocumentTests.cs`, `ClientReportBuilderTests.cs`

**Interfaces:**
- Consumes: `SenderDecisionSummaries.Summarise` (Task 1); `Domain.Groups` (nav collection of `Group` with `Id`).
- Produces:
  - `ClientReportDomain.Senders` (`SenderDecisionSummary?`, null when no decisions were supplied).
  - `public static IReadOnlyList<string> ClientReportDocument.UnauthorisedSentences(SenderDecisionSummary summary, CultureInfo culture)` (public so tests can assert wording without parsing a PDF). Returns an empty list when `UnauthorisedBlocked + UnauthorisedDelivered == 0`.

- [ ] **Step 1: Write the failing tests.**
  - Calculator: `Build_FillsSenderSummaryFromDecisionsForTheDomainsGroups` (one domain in group 7, decision `Scope=Group, GroupId=7` Unauthorised for a service present in its in-period reports → `Senders.UnauthorisedBlocked` equals that service's blocked messages) and `Build_LeavesSendersNullWhenNoDecisionsAreSupplied`. Also `Build_IgnoresReportsOutsideThePeriodForSenderCounts` (an unauthorised sender only in the comparison period → zeros).
  - Document sentences (exact text):
    - blocked only, 120 → `["120 messages from senders you haven't authorised were blocked."]`
    - 1 blocked → `["1 message from senders you haven't authorised was blocked."]`
    - delivered failing 40, blocked 0 → `["40 messages from senders you haven't authorised were still delivered. Moving the DMARC policy to quarantine or reject would block them."]`
    - blocked 10 plus delivered failing 5 → both sentences, blocked first
    - delivered 9, delivered failing 0 → `["9 messages from senders you haven't authorised were delivered. They pass DMARC, so we are checking whether they should be authorised."]`
    - all zero → `[]`
  - Builder (Postgres fixture, extend the existing builder test class): `BuildAsync_LoadsDecisionsForTheGroupDomainsAndBaseline` seeds a baseline Unauthorised decision for a service present in a group domain's reports and a decision on a domain in another group, and asserts only the first is reflected in `Senders`.
  - Document: `Render_ShowsNothingForADomainWithNoUnauthorisedMail` using the existing document test helpers (the PDF renders without the phrase "haven't authorised" in its extracted text, if the existing tests extract text; otherwise assert only that `UnauthorisedSentences` is empty and that rendering does not throw).

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientReport"`
Expected: build errors for the new members, then FAIL.

- [ ] **Step 3: Implement.**
  - `ClientReport.cs` and `ClientReportCalculator.cs`: add the optional parameters; in `Build`, when `Decisions` is not null, call `Summarise(inPeriod, ownerByIp, decisions, domain.Id, domain.Groups.Select(group => group.Id).ToList())` per domain, where `ownerByIp` is built from `inputs.Owners` the same way `TopSenders` builds it (extract that dictionary to a small private helper rather than duplicating it).
  - `ClientReportBuilder.cs`: load `domains` with `.Include(domain => domain.Groups)` and load decisions with one query: baseline, plus domain scope where `DomainId` is in `domainIds`, plus group scope where `GroupId` is in the ids of all groups those domains belong to; pass them as `Decisions`. Add `.AsSplitQuery()` if the new include touches a query with another collection include (it does not on `domains`, which has none).
  - `ClientReportDocument.cs`: add `UnauthorisedSentences` with the exact wording above (counts through the existing `Count(culture, n)` helper, singular when 1), and in `AddDomain`, directly after the "What receivers did" block, add each sentence as a paragraph when `domain.Senders` is not null.

- [ ] **Step 4: Run to verify GREEN**

Run: the Step 2 command. Expected: all `ClientReport` tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Reporting/ClientReports test/DotMarc.Tests/Reporting/ClientReports
git commit -m "Say in the client report how much mail from unauthorised senders was blocked"
```

---

### Task 3: The sources API decision field

**Files:**
- Modify: `src/DotMarc/Api/ApiModels.cs:110` (`ApiSourceDetail` gains `string? Decision = null` after `Service`), `src/DotMarc/Api/SourceEndpoints.cs` (page items), `website/data/openapi/dotmarc-api.json` (regenerated), the API docs page that documents the sources endpoint (find with `grep -rn "service" website/docs/api*.mdx`; add the field)
- Test: `test/DotMarc.Tests/Api/SourceEndpointTests.cs`

**Interfaces:**
- Consumes: `SenderDecisionService.LoadForDomainAsync(DotMarcDbContext, int)` returning the domain's, its groups' and the baseline decisions; `SenderDecisionResolver.Resolve`; the endpoint's existing `serviceByIp` and `domainId`.
- Produces: `ApiSourceDetail.Decision`: one of `Authorised`, `UnderReview`, `Unauthorised`, `Unreviewed` (enum name, `Unreviewed` when no decision applies). Documented in the record's summary comment.

- [ ] **Step 1: Write the failing tests** in `SourceEndpointTests`, following that file's existing seeding and client helpers:
  - `ListSources_ReturnsUnreviewedWhenNoDecisionApplies`
  - `ListSources_ReturnsTheDomainDecisionForAServiceMatchedIp` (domain-level Unauthorised on the service → `"Unauthorised"`)
  - `ListSources_AnIpDecisionOverridesTheServiceDecision` (service Authorised, IP Unauthorised → that row `"Unauthorised"`)
  - `ListSources_UsesTheBaselineAndGroupDecisions` (baseline UnderReview → `"UnderReview"`; then a group decision on a group the domain is in overrides it)
  - `ListSources_ReturnsUnreviewedForAnUnmatchedIpWithOnlyAServiceBaseline` (no service for the IP, baseline entry exists for some service → `"Unreviewed"`)

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SourceEndpointTests"`
Expected: build error (no `Decision`), then FAIL.

- [ ] **Step 3: Implement.** In `ListSourcesAsync`, load `decisions = await SenderDecisionService.LoadForDomainAsync(context, domainId.Value, cancellationToken)` and the domain's group ids (a one-line query on `context.Domains`, same as `LoadForDomainAsync` uses internally; if that is duplicated, make `LoadForDomainAsync` return the ids through a new overload `LoadForDomainWithGroupsAsync` returning `(List<SenderDecision> Decisions, List<int> GroupIds)` and have the existing method call it). For each item set `Decision` from `SenderDecisionResolver.Resolve(decisions, domainId.Value, groupIds, serviceByIp.GetValueOrDefault(ip), ip, new Dictionary<int,string>()).Status?.ToString() ?? "Unreviewed"`. Update the record's summary comment to describe `Decision` and its four values.

- [ ] **Step 4: Regenerate the OpenAPI copy and run tests**

Run: `node scripts/update-openapi.mjs` then `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SourceEndpointTests|FullyQualifiedName~OpenApi"`
Expected: all pass (the OpenAPI test fails until the copy is regenerated).

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Api src/DotMarc/Data/SenderDecisionService.cs website/data/openapi website/docs test/DotMarc.Tests/Api
git commit -m "Show each source's sender decision in the sources API"
```

---

### Task 4: The Overview count, docs and demo check

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor` (Overview panel near line 58-64, plus a `_unreviewedSenders` field refreshed where `_senderResult` and `_decisions` are rebuilt, `RebuildSenders()` and after `DecideAsync`), `website/docs/getting-started.mdx`, `website/docs/client-reports.mdx` (find the actual file name with `grep -rln "What receivers did\|client report" website/docs`)
- Test: `test/DotMarc.Tests/Components/` (the existing bUnit or component test location for DomainDetail if one exists; otherwise cover the count through Task 1's tests and verify in the browser)

**Interfaces:**
- Consumes: `SenderDecisionSummaries.Summarise` (Task 1), `_domain.Reports`, `_decisions`, `_groupNames`, the page's existing owner lookup used by `RebuildSenders()`, and the `_activeTabIndex` tab state.
- Produces: an Overview line "N senders to review" (singular "1 sender to review") shown only when N > 0, with a "Review" button that switches to the Sources tab (index of the Sources `MudTabPanel`; reuse whatever method `OnActiveTabIndexChangedAsync` expects).

- [ ] **Step 1: Write the failing test if a DomainDetail component test exists** (`DomainDetail_Overview_ShowsUnreviewedSenderCount`, with a seeded domain whose reports include one unmatched IP and no decisions, asserting the text "1 sender to review" and that it disappears after a decision is saved). If the repo has no DomainDetail component tests, skip to Step 3 and verify in the browser in Step 5.

- [ ] **Step 2: Run to verify RED** (only if Step 1 wrote a test).

- [ ] **Step 3: Implement.** Compute `_unreviewedSenders` from `Summarise(...).UnreviewedSenders` in the same place `_senderResult` is rebuilt, using the same owner-by-IP dictionary `RebuildSenders()` already builds, so the count updates when enrichment finishes and when a decision is saved. Render it as a `MudAlert` (Severity.Info, Dense) under the three stat items, matching the style of the existing Overview alerts, with a `Review` button calling the tab-switch.

- [ ] **Step 4: Docs.** In `getting-started.mdx`, under "Decide which senders are authorised", add that the Overview shows how many senders still need a decision, that the sources API returns each source's `decision`, and that client reports say how much mail from unauthorised senders was blocked or delivered. In the client reports page, document the two sentences with their exact wording. No em dashes.

- [ ] **Step 5: Verify in the browser** (playwright-edge, per the repo's Docker postgres:18 on port 55432 recipe with `Demo__Enabled=true`): shop.aurora-retail.example Overview shows "1 sender to review" (the SendGrid demo source) and the button opens the Sources tab; marking SendGrid Under review makes the line disappear without a reload. cobalt-freight.example Overview shows no line (all its senders are decided). Stop the app and container afterwards.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test test/DotMarc.Tests`
Expected: 0 failed. Then:

```bash
git add src/DotMarc/Components/Pages/DomainDetail.razor website/docs test
git commit -m "Show how many senders need a decision on the domain Overview"
```
