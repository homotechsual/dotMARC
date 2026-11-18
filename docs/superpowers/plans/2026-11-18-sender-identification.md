# Sender Identification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show, per domain, which sending services its DMARC reports point to (with evidence strength, volume and failures per service) and use the same attribution in recommendations, the client report, the portal and the API.

**Architecture:** A static catalogue of services and their signatures, a pure scoring function over one source, and a pure roll-up over the 30-day reports. Nothing is stored: the Sources tab, client report builder, portal and API compute it from records they already load. See the spec for the reasoning.

**Tech Stack:** C# / .NET 10, Blazor Server + MudBlazor 9, xUnit. No bUnit in this repo, so UI is verified by running the app.

**Spec:** `docs/superpowers/specs/2026-11-18-sender-identification-design.md`

## Global Constraints

- No database change, no migration, no new settings.
- Evidence strength is a score capped at 0.99 and labelled "Evidence" in the UI, never "probability" or "confidence". A source is attributed only at 0.30 or more, otherwise it is Unrecognised.
- Weights (one place, constants in `SenderScoring`): auth domain match with passing check 0.90, with failing check 0.80, DKIM selector 0.60, IP owner 0.50, domain's own SPF include 0.20. Each signal type counts once per service per source; combine with `1 - product(1 - weight)`.
- Bands: 0.70 and above strong, 0.40 to 0.69 moderate, below 0.40 weak.
- Service names must match the existing ones exactly: the 13 in `DomainStatistics.KnownEmailServiceProviders` (including "Mandrill (Mailchimp Transactional)" and "Salesforce Marketing Cloud") and the names in `MailServiceDetector` / `SpfIncludeCatalog`.
- No new outbound lookups. Fix text names the service and the mechanism, never exact SPF include or DKIM selector values.
- No em dashes in any copy or docs. Failure text says the cause first, then the fix. Meaningful variable names, even in tests.
- Message sums use `long`.

## Review Focus

- A source whose IP owner has not been looked up yet (`Organization` null, enrichment pending): must render as Unrecognised or by its other signals, never throw, and re-attribute when the owner arrives.
- A properly aligned sender signs as the customer's own domain, so its auth domains match nothing. It must still be found through IP owner and selector.
- A source matching two services at the same score: the result must be deterministic (higher score, then catalogue order).
- A domain with no reports, or only passing mail: empty or zero-failure result, no Unrecognised row, no divide by zero.
- Records with no `AuthDetails` (ingested before backfill): no crash, failure cause falls back to Both.
- Case differences in domains, selectors and owner names.

---

### Task 1: Catalogue and selector on auth details

**Files:**
- Create: `src/DotMarc/Reporting/Senders/SenderServiceCatalog.cs`
- Modify: `src/DotMarc/Reporting/DomainStatistics.cs` (`AuthDetailSummary` at the bottom, and its construction in `GetSourceAggregates` and `GetReasonBreakdown`)
- Test: `test/DotMarc.Tests/Reporting/SenderServiceCatalogTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SenderService(string Name, IReadOnlyList<string> AuthDomainSuffixes, IReadOnlyList<string> DkimSelectorPatterns, IReadOnlyList<string> IpOwnerKeywords, IReadOnlyList<string> DetectedNames)` in namespace `DotMarc.Reporting`
  - `public static class SenderServiceCatalog` with `IReadOnlyList<SenderService> All`, `bool MatchesAuthDomain(SenderService service, string domain)`, `bool MatchesSelector(SenderService service, string? selector)`, `bool MatchesOwner(SenderService service, string? owner)`, `string? IdentifyByAuthDomains(IEnumerable<string> domains)`
  - `AuthDetailSummary(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result, string? Selector = null)`

- [ ] **Step 1: Write the failing tests** in `SenderServiceCatalogTests`:
  - `Names_AreUnique_AndEveryServiceHasAtLeastOneSignature`
  - `MatchesAuthDomain_MatchesTheDomainItselfAndSubdomains_CaseInsensitively_ButNotLookalikes` (`bounce.mcsv.net` and `MCSV.NET` match Mailchimp, `notmcsv.net` does not)
  - `MatchesSelector_IsAnchoredAndCaseInsensitive` (`selector1` and `Selector2` match Microsoft 365, `selector10` does not)
  - `MatchesOwner_IsCaseInsensitiveContains` (`"Microsoft Corporation"` matches Microsoft 365)
  - `IdentifyByAuthDomains_ReturnsTheExistingNames` (a `.sendgrid.net` domain gives "SendGrid", `.mandrillapp.com` gives "Mandrill (Mailchimp Transactional)")
  - `EveryNameInMailServiceDetectorAndSpfIncludeCatalog_IsInTheCatalogue` (via `DetectedNames` or `Name`)
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter SenderServiceCatalogTests`. Expected: FAIL, types missing.
- [ ] **Step 3: Implement the catalogue.** Seed it with the 13 existing services plus Microsoft 365, Google Workspace, Zoho (`DetectedNames` "Zoho" and "Zoho Mail"), Fastmail and Proton Mail. Auth-domain suffixes are written without a leading dot and match the host itself or any subdomain. Microsoft 365: `onmicrosoft.com`, `outlook.com`; Google Workspace: `google.com`, `gmail.com`, `googlemail.com`. Selectors as anchored regexes: Microsoft 365 `^selector[12]$`, Google `^google$`, Mailchimp `^k[1-3]$`, Mandrill `^mte[12]$`, SendGrid `^s[12]$`, Zendesk `^zendesk[12]$`, HubSpot `^hs[12]$`. Owner keywords: `Microsoft`, `Google`, `Rocket Science`, `SendGrid`, `Amazon`, `Salesforce`, `Mailgun`, `Zendesk`, `HubSpot`, `Zoho`, `Fastmail`, `Proton`.
- [ ] **Step 4: Add `Selector` to `AuthDetailSummary`** as an optional last parameter and pass `d.Selector` where it is constructed from `ReportRecordAuthDetail`. Existing three-argument callers must keep compiling.
- [ ] **Step 5: Run** the new tests plus `--filter DomainStatisticsTests`. Expected: PASS.
- [ ] **Step 6: Commit** `git commit -m "Add the sender service catalogue and carry the DKIM selector on auth details"`

---

### Task 2: Scoring one source

**Files:**
- Create: `src/DotMarc/Reporting/Senders/SenderScoring.cs`
- Test: `test/DotMarc.Tests/Reporting/SenderScoringTests.cs`

**Interfaces:**
- Consumes: `SenderServiceCatalog`, `AuthDetailSummary` from Task 1.
- Produces, in namespace `DotMarc.Reporting`:
  - `public enum SenderSignal { AuthDomainPassed, AuthDomainFailed, DkimSelector, IpOwner, SpfInclude }`
  - `public sealed record SenderEvidence(SenderSignal Signal, string Description)`
  - `public sealed record ServiceScore(SenderService Service, double Score, IReadOnlyList<SenderEvidence> Evidence)`
  - `public sealed record SenderSourceInput(string SourceIp, string? Owner, IReadOnlyList<AuthDetailSummary> AuthDetails)`
  - `public static class SenderScoring` with constants `Cap = 0.99`, `AttributionThreshold = 0.30`, `StrongFrom = 0.70`, `ModerateFrom = 0.40`, the five weight constants, and `IReadOnlyList<ServiceScore> Score(SenderSourceInput source, IReadOnlyCollection<string> spfIncludedNames)` returning only services with a score above zero, highest first, ties by catalogue order.

- [ ] **Step 1: Write the failing tests** (`SenderScoringTests`, using `Assert.Equal(expected, actual, precision: 3)`):
  - `AuthDomainThatPassed_ScoresPointNine` and `AuthDomainThatFailed_ScoresPointEight`
  - `SelectorAlone_ScoresPointSix`, `OwnerAlone_ScoresPointFive`, `SpfIncludeAlone_ScoresPointTwo` (the include matches by `DetectedNames`)
  - `TwoSignals_Combine`: selector and owner for Microsoft 365 give `1 - 0.4 * 0.5 = 0.8`
  - `AllFiveSignals_AreCappedAtPointNinetyNine`
  - `ASignalTypeIsCountedOncePerService`: two passing `.mcsv.net` domains still give 0.9
  - `NoSignals_ReturnsEmpty`; `NullOwner_DoesNotThrow`
  - `TwoServicesAtTheSameScore_AreOrderedByCatalogueOrder`
  - `EvidenceDescriptions_ReadLikeSentences` (for example "DKIM signed by mcsv.net and passed", "IP owned by Google LLC")
- [ ] **Step 2: Run** `--filter SenderScoringTests`. Expected: FAIL.
- [ ] **Step 3: Implement `SenderScoring.Score`.** For the auth-domain signal take the strongest matching detail per service (passed beats failed). Use the Mechanism in the description ("SPF" or "DKIM").
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Score the evidence that a source belongs to a sending service"`

---

### Task 3: Roll-up per service with failures

**Files:**
- Create: `src/DotMarc/Reporting/Senders/SenderIdentification.cs`
- Modify: `src/DotMarc/Reporting/DomainStatistics.cs` (add the public cause classifier next to `ClassifyFailureCategory`)
- Test: `test/DotMarc.Tests/Reporting/SenderIdentificationTests.cs`

**Interfaces:**
- Consumes: `SenderScoring.Score`, `SenderServiceCatalog.All`, `AuthDetailSummary`, `DomainStatistics.IsPassing`.
- Produces, in namespace `DotMarc.Reporting`:
  - `public enum SenderFailureCause { SpfNotAligned, DkimNotAligned, DkimMissing, Both }`
  - `DomainStatistics.ClassifyFailureCause(IReadOnlyList<AuthDetailSummary> authDetails, string headerFrom)` returning `SenderFailureCause`. With the existing outcomes: DKIM `NotCovered` and SPF not `NotCovered` gives `DkimMissing`; DKIM `Misaligned` and SPF `NotCovered` or `Failed` gives `DkimNotAligned`; SPF `Misaligned` and DKIM `Failed` gives `SpfNotAligned`; anything else gives `Both`.
  - `public sealed record SenderServiceRow(string Name, bool IsUnrecognised, long Messages, double Share, double? Evidence, double? PassRate, long Passing, long Failing, long ActedOn, SenderFailureCause? DominantCause, string? Fix, IReadOnlyList<string> EvidenceLines, IReadOnlyList<string> SourceIps)`
  - `public sealed record SenderIdentificationResult(IReadOnlyList<SenderServiceRow> Services, SenderServiceRow? Unrecognised, IReadOnlyDictionary<string, string> ServiceByIp)`
  - `public static class SenderIdentification` with `SenderIdentificationResult Identify(IEnumerable<Report> reportsInWindow, IReadOnlyDictionary<string, string?> ownerByIp, IReadOnlyCollection<string> spfIncludedNames)` and `string FixText(string serviceName, SenderFailureCause cause)`.

- [ ] **Step 1: Write the failing tests** (`SenderIdentificationTests`, building `Report` and `ReportRecord` objects the way `DomainStatisticsTests` does):
  - `ClassifyFailureCause_FollowsTheTable` (theory over the five rows above)
  - `ASourceWithPassingMailchimpDkimDomain_IsAttributedToMailchimp_WithItsVolumeShareAndPassRate`
  - `AnAlignedMicrosoftSender_IsFoundThroughOwnerAndSelector_WhenAuthDomainsAreTheCustomersOwn`
  - `FailingMessages_AreGroupedPerService_WithTheDominantCauseAndFixNamingTheService`
  - `ActedOn_CountsQuarantineAndRejectSeparatelyFromFailing`
  - `ASourceBelowThirtyPercent_GoesToUnrecognised_AndIsTheLastRow`
  - `AServiceRowEvidence_IsTheHighestSourceScore`; `ServiceByIp_MapsEveryAttributedIp`
  - `NoReports_GivesNoRowsAndNoUnrecognised`; `OnlyPassingMail_HasZeroFailingAndNoFix`
  - `RecordsWithoutAuthDetails_DoNotThrow_AndFallBackToBoth`
  - `SharesAcrossRowsAddUpToOne`
- [ ] **Step 2: Run** `--filter SenderIdentificationTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Score each source IP once from its distinct auth details across all its records. Attribute to the top score of at least `AttributionThreshold`. A record is failing when `!DomainStatistics.IsPassing(record)`; its cause comes from `ClassifyFailureCause` on that record's own details. `DominantCause` is the cause with the most failing messages. `FixText` is one template per cause that names the service and the mechanism to fix (for example "Mailchimp sends mail whose DKIM isn't aligned with your domain. Ask them to sign with your domain, then recheck."). `Fix` is null when `Failing` is 0.
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Group a domain's sending sources by service, with failures and fixes"`

---

### Task 4: Recommendations use the catalogue

**Files:**
- Modify: `src/DotMarc/Reporting/DomainStatistics.cs` (remove `KnownEmailServiceProviders` and `IdentifyKnownEsp`; `BuildRecommendation` calls `SenderServiceCatalog.IdentifyByAuthDomains`)
- Test: `test/DotMarc.Tests/Reporting/DomainStatisticsTests.cs`

**Interfaces:**
- Consumes: `SenderServiceCatalog.IdentifyByAuthDomains`.

- [ ] **Step 1: Add a failing test** `Recommendation_NamesAMicrosoftOrGoogleSender_WhichTheOldSuffixListMissed` using an auth domain under `google.com`.
- [ ] **Step 2: Run** `--filter DomainStatisticsTests`. Expected: the new test FAILS.
- [ ] **Step 3: Replace the private list and method** with the catalogue call. The existing recommendation tests must pass unchanged.
- [ ] **Step 4: Run** `--filter DomainStatisticsTests`. Expected: all PASS.
- [ ] **Step 5: Commit** `git commit -m "Name sending services in recommendations from one catalogue"`

---

### Task 5: Senders table on the Sources tab

**Files:**
- Create: `src/DotMarc/Components/Shared/SenderServicesTable.razor`
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor` (Sources tab at about line 373; `SourceRow`; the enrichment and detection completions)

**Interfaces:**
- Consumes: `SenderIdentificationResult`, `SenderServiceRow`, `SenderScoring.StrongFrom` and `ModerateFrom`.
- Produces: `SenderServicesTable` with `[Parameter] public SenderIdentificationResult? Result { get; set; }`.

- [ ] **Step 1: Build `SenderServicesTable`.** A `MudSimpleTable` with columns Service, Messages, Share, Evidence, Pass rate, Failing, Fix. Evidence shows a percentage and a chip coloured by band (strong, moderate, weak), with the label "Evidence". Each row expands (a chevron `MudIconButton`, lowercase `title`) to show `EvidenceLines` and `SourceIps`. Unrecognised is the last row with the text "Not matched to a known service". An empty result shows "No sending sources in the last 30 days." Use enums or constants for anything that would need nested quotes in a Razor attribute.
- [ ] **Step 2: Wire it into `DomainDetail.razor`.** Add `_senderResult` and `RebuildSenders()`, which builds `ownerByIp` from `_sources` and `spfIncludedNames` from `_detectedMailServices` of kind `Sending` (their `ProviderName`), then calls `SenderIdentification.Identify(_domain.Reports, ...)`. Call it at the end of load, after each source's enrichment completes, and after detection completes, each followed by `StateHasChanged` the way those methods already do. Render the table above the existing per-IP table inside the Sources panel, and add a sortable Service column to the per-IP table from `ServiceByIp`.
- [ ] **Step 3: Build** `dotnet build src/DotMarc`. Expected: no errors or warnings.
- [ ] **Step 4: Verify in the running app.** Start the demo stack (see `docs` for the demo command), open a demo domain's Sources tab with Playwright (Edge), and check: the table renders, owners arriving later re-attribute rows, expanding a row shows evidence, Unrecognised is last, and light and dark themes both read well. Screenshot and look at it.
- [ ] **Step 5: Commit** `git commit -m "Show the sending services on a domain's Sources tab"`

---

### Task 6: Client report names failing services

**Files:**
- Modify: `src/DotMarc/Reporting/ClientReports/ClientReportBuilder.cs` (load `AuthDetails` with the records), `ClientReport.cs` (`ClientReportSender` gains `string? Service`), `ClientReportCalculator.cs` (`TopSenders`), `ClientReportNextSteps.cs` (line 45 to 48), `ClientReportDocument.cs` (line 221)
- Test: existing client report tests, plus new cases

**Interfaces:**
- Consumes: `SenderIdentification.Identify`, `SenderServiceRow`.
- Produces: `ClientReportSender(string Ip, string? Owner, long Messages, long Passing, long Failing, double Share, string? Service = null)`.

- [ ] **Step 1: Write failing tests:** the calculator sets `Service` for a source with a Mailchimp auth domain; the next-step text reads "Mailchimp sent N messages as example.com that failed DMARC. ..." when a service is known and keeps today's owner text when only the owner is known; the document row shows "Mailchimp (203.0.113.9)".
- [ ] **Step 2: Run** `--filter ClientReport`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement.** Add `.ThenInclude`-style loading of `Records.AuthDetails` in the builder. In `TopSenders`, run `SenderIdentification.Identify` for the domain's period reports with an empty `spfIncludedNames` and set `Service` from `ServiceByIp`. Prefer `Service` over `Owner` in the next-step sentence and the document row.
- [ ] **Step 4: Run** `--filter ClientReport`. Expected: all PASS.
- [ ] **Step 5: Commit** `git commit -m "Name the sending service in client report conclusions"`

---

### Task 7: Portal and API

**Files:**
- Modify: `src/DotMarc/Portal/PortalData.cs` (top sources), `src/DotMarc/Api/ApiModels.cs:109` (`ApiSourceDetail` gains `string? Service`), `src/DotMarc/Api/SourceEndpoints.cs`, `website/data/openapi/dotmarc-api.json` (regenerate)
- Test: `test/DotMarc.Tests/Api/SourceEndpointTests.cs`, a portal test beside the existing ones

**Interfaces:**
- Consumes: `SenderIdentification.Identify`, `SenderIdentificationResult.ServiceByIp`.

- [ ] **Step 1: Write failing tests:** the sources endpoint returns `service: "Mailchimp"` for a source with a Mailchimp auth domain and `null` for an unmatched one; the portal's top sources carry the service name.
- [ ] **Step 2: Run** `--filter "SourceEndpointTests|Portal"`. Expected: FAIL.
- [ ] **Step 3: Implement.** The API pages in SQL, so after the page query load the page's IPs' records with `AuthDetails` for the same range, run `Identify`, and map `ServiceByIp` onto the items. Add a description for the field in the OpenAPI text, then run `node scripts/update-openapi.mjs`.
- [ ] **Step 4: Run** the filtered tests and `--filter OpenApiDocumentTests`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Include the sending service in the sources API and the portal"`

---

### Task 8: Docs and full verification

**Files:**
- Modify: the website docs page that describes the Sources tab (find it with Grep for "Sources" under `website/docs`), and `README` feature lists only if they already name per-source detail.

- [ ] **Step 1: Document** the Senders table: what each column means, that Evidence is a score of the signals found (list them), that Unrecognised means no match rather than a bad sender, and that attribution is recomputed each time, so it improves as the catalogue grows. No em dashes.
- [ ] **Step 2: Run the full suite** `dotnet test test/DotMarc.Tests`. Expected: all PASS, including the earlier 2110 tests.
- [ ] **Step 3: Commit** `git commit -m "Document the sending services table"`
