# Sender Matching Phase 1 (SPF Ranges) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Attribute a source to a sending service with strong evidence when its IP falls inside a range the domain's own SPF record authorises for that service.

**Architecture:** The daily SPF check also expands the record (following `include:` and `redirect=`), keeps each `ip4:` and `ip6:` range tagged with the include it came from and the catalogue service that include belongs to, and stores the list on the domain. `SenderScoring.Score` gains an SPF-range signal (0.85) that replaces the name-only SPF include signal (0.20) for the same service. Every caller of `SenderIdentification` passes the domain's stored ranges, so the Sources table, alerts, client report, portal and API agree.

**Tech Stack:** .NET 10, EF Core + Npgsql (one migration), Blazor Server + MudBlazor 9, xUnit with the Postgres fixture.

**Spec:** `docs/superpowers/specs/2026-11-18-sender-matching-accuracy-design.md` (sections "SPF ranges", "New signals and weights", "Testing" and "Phasing", item 1). Reuses `SpfRecord`, `SpfTerm`, `ITxtRecordLookup`, `SpfDnsChecker`, `SenderScoring` and `SenderServiceCatalog`.

## Global Constraints

- No em dashes in any copy or docs.
- Weight `SpfRangeWeight = 0.85`, combined as before (`1 - product(1 - weight)`, capped at 0.99). When the range signal matches a service, the name-only `SpfInclude` signal for that service is not added (the larger one is used, not both).
- Evidence wording says "authorises", never "confirms": "Your SPF record authorises this IP for Mailchimp".
- Expansion limits: depth cap 10, at most 50 DNS lookups per domain, a visited path against loops, a failing lookup keeps the last good stored result.
- Only terms with the `+` qualifier (written or implied) count as authorising. `-ip4`, `~ip4` and `?ip4` are skipped.
- No new outbound traffic beyond the TXT lookups the SPF check already makes. Nothing runs on the render path.
- No change to decisions, alerts or reports themselves; they get the better attribution through `SenderIdentification`.
- Commit message style: short imperative sentence, no prefix, as in `git log`.

## Review Focus

- **IPv6 and IPv4-mapped IPv6 source IPs:** `::ffff:192.0.2.5` must match an `ip4:192.0.2.0/24` range; a plain IPv6 address must never match an IPv4 range. Task 1.
- **Malformed mechanisms:** `ip4:999.1.1.1`, `ip4:1.2.3.4/33`, `ip6:zzz` are skipped quietly and never throw. Tasks 1 and 3.
- **Negative qualifiers:** `-ip4:192.0.2.0/24` must not attribute that range to anyone. Task 3.
- **Loops and hostile records:** an include that points back at itself, a `redirect=` to itself and a record with 80 includes all terminate within the lookup cap. Task 3.
- **A failed lookup:** a DNS error during expansion keeps the previously stored ranges instead of wiping them. Task 4.
- **A domain with no stored ranges:** scores and attribution are identical to today. Tasks 5 and 6.
- **An IP in two services' ranges:** both get the evidence; the higher score wins, ties by catalogue order. Task 5.

## File Structure

- Create `src/DotMarc/Dns/CidrRange.cs`: parse and contain-check one range.
- Create `src/DotMarc/Dns/SpfRangeExpander.cs`: expansion plus the `SpfServiceRange` record and its JSON helpers.
- Modify `src/DotMarc/Reporting/Senders/SenderServiceCatalog.cs`: `SpfIncludeHosts` per service.
- Modify `src/DotMarc/Dns/SpfCheckResult.cs`, `SpfDnsChecker.cs`, `src/DotMarc/Ingestion/PollingService.cs`, `src/DotMarc/Data/Domain.cs`: expand during the check and store.
- Modify `src/DotMarc/Reporting/Senders/SenderScoring.cs`, `SenderIdentification.cs`: the signal.
- Modify the callers listed in Task 6.
- Tests under `test/DotMarc.Tests/Dns/` and `test/DotMarc.Tests/Reporting/` (flat, as the existing sender tests).

---

### Task 1: CIDR containment

**Files:**
- Create: `src/DotMarc/Dns/CidrRange.cs`
- Test: `test/DotMarc.Tests/Dns/CidrRangeTests.cs`

**Interfaces:**
- Produces: `public readonly record struct CidrRange(IPAddress Network, int PrefixLength)` with `static bool TryParse(string? text, out CidrRange range)` and `bool Contains(IPAddress address)`. A bare address with no `/` is a single-host range (/32 or /128). `ToString()` returns `"192.0.2.0/24"` form.

- [ ] **Step 1: Write the failing tests** (`CidrRangeTests`), all as `[Theory]`/`[Fact]` over `TryParse` then `Contains`:
  - `192.0.2.0/24` contains `192.0.2.0`, `192.0.2.255`; not `192.0.3.0`, not `192.0.1.255`.
  - `192.0.2.7` (no prefix) contains only `192.0.2.7`.
  - `0.0.0.0/0` contains any IPv4; `/32` and `/0` edges.
  - `2001:db8::/32` contains `2001:db8:ffff::1`; not `2001:db9::1`.
  - IPv4-mapped: `::ffff:192.0.2.5` is contained by `192.0.2.0/24`.
  - An IPv6 address is never contained by an IPv4 range and vice versa.
  - `TryParse` false for `null`, `""`, `"999.1.1.1"`, `"1.2.3.4/33"`, `"1.2.3.4/-1"`, `"::1/129"`, `"zzz"`, `"1.2.3.4/"`.
  - A range written with host bits set (`192.0.2.77/24`) matches the same addresses as `192.0.2.0/24`.
- [ ] **Step 2: Run, expect FAIL** (type not defined): `dotnet test test/DotMarc.Tests --filter CidrRangeTests`.
- [ ] **Step 3: Implement `CidrRange`** in `src/DotMarc/Dns/CidrRange.cs`. Compare by masking the address bytes against the prefix length; map IPv4-mapped IPv6 candidates to IPv4 before comparing; reject a prefix longer than the family allows.
- [ ] **Step 4: Run the tests, expect PASS.**
- [ ] **Step 5: Commit** `Add CIDR range parsing and containment`.

### Task 2: Catalogue SPF include hosts

**Files:**
- Modify: `src/DotMarc/Reporting/Senders/SenderServiceCatalog.cs`
- Test: `test/DotMarc.Tests/Reporting/SenderServiceCatalogTests.cs` (extend or create)

**Interfaces:**
- Produces: `SenderService` gains a last positional member `IReadOnlyList<string> SpfIncludeHosts`; the private `Service(...)` helper gains an optional `spfIncludes` parameter. `public static string? SenderServiceCatalog.ServiceForSpfInclude(string host)` returns the service name whose hosts match the host by exact name or as a suffix (`host == entry || host.EndsWith("." + entry)`), case-insensitive, ignoring a trailing dot, or null.

- [ ] **Step 1: Write the failing tests:**
  - Every `SpfIncludeCatalog.All` include maps to the catalogue service with the matching name, except `("Zoho (EU)", "zoho.eu")` which maps to `Zoho`.
  - `ServiceForSpfInclude("spf.protection.outlook.com")` is `Microsoft 365`; `"_spf.google.com"` is `Google Workspace`; `"SERVERS.MCSV.NET."` is `Mailchimp`; `"mail.unknown.example"` is null; `"notgoogle.com"` does not match a `google.com` entry.
  - Seed hosts beyond the editor list, so the common nested includes tag too: Microsoft `spf.protection.outlook.com`; Google `_spf.google.com`, `_netblocks.google.com`; SendGrid `sendgrid.net`; Mailgun `mailgun.org`; Mailchimp `servers.mcsv.net`; Mandrill `spf.mandrillapp.com`; Postmark `spf.mtasv.net`; SparkPost `sparkpostmail.com`; HubSpot `hubspotemail.net`; Zendesk `mail.zendesk.com`; Freshdesk `email.freshdesk.com`; Amazon SES `amazonses.com`; Salesforce `_spf.salesforce.com`; Constant Contact `spf.constantcontact.com`. Test one assertion per service named here.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** the member, the helper parameter, the data above and `ServiceForSpfInclude`.
- [ ] **Step 4: Run these tests plus the existing `SenderIdentificationTests` and `SenderScoringTests`, expect PASS.**
- [ ] **Step 5: Commit** `Tie SPF include hosts to catalogue services`.

### Task 3: SPF range expansion

**Files:**
- Create: `src/DotMarc/Dns/SpfRangeExpander.cs`
- Test: `test/DotMarc.Tests/Dns/SpfRangeExpanderTests.cs`

**Interfaces:**
- Consumes: `ITxtRecordLookup.GetTxtValuesAsync`, `SpfRecord.Parse` and `SpfRecord.IsSpf`, `SpfTerm` (`Name`, `Argument`, `Qualifier`, `Target`, `IsInclude`, `IsRedirect`), `CidrRange.TryParse`, `SenderServiceCatalog.ServiceForSpfInclude`.
- Produces:
  - `public sealed record SpfServiceRange(string? Service, string Include, string Cidr)`: `Include` is the host of the domain's top-level include the range came from (for `redirect=`, the redirect target; for the domain's own `ip4:` terms, the domain name itself) and `Service` is the catalogue service, found by testing each host on the path from that top-level include down to where the range was found, outermost first, null when none match.
  - `public sealed class SpfRangeExpander(ITxtRecordLookup lookup)` with `Task<IReadOnlyList<SpfServiceRange>?> ExpandAsync(string domainName, SpfRecord record, CancellationToken cancellationToken)`: returns null if any lookup throws `HttpRequestException`, `TaskCanceledException` (not caused by the caller's token) or `JsonException`. Constants `MaxLookups = 50`, `MaxDepth = 10`.
  - `public static class SpfServiceRanges` with `string Serialize(IReadOnlyList<SpfServiceRange>)` and `IReadOnlyList<SpfServiceRange> Parse(string? json)` (null or malformed JSON gives an empty list, never throws), and `ServiceForIp(IReadOnlyList<SpfServiceRange> ranges, string sourceIp)` is not needed here (Task 5 does the matching).

- [ ] **Step 1: Write the failing tests** with a fake `ITxtRecordLookup` (a dictionary of name to TXT values; a name set to throw `HttpRequestException`):
  - `v=spf1 ip4:192.0.2.0/24 -all` at `example.com` gives one range, `Service` null, `Include` `example.com`.
  - `v=spf1 include:servers.mcsv.net -all` where that record is `v=spf1 ip4:198.51.100.0/24 ip6:2001:db8::/32 ~all` gives two ranges tagged `Mailchimp` with `Include` `servers.mcsv.net`.
  - Nested: `include:_spf.google.com` pointing to `include:_netblocks.google.com` pointing to `ip4:...` is tagged `Google Workspace`.
  - An include to an unknown host is kept with `Service` null and its host in `Include`.
  - `redirect=other.example` expands the target's record, tagged by the target.
  - `-ip4:192.0.2.0/24`, `~ip4:...` and `?ip4:...` are skipped; `+ip4:...` and `ip4:...` kept.
  - Malformed `ip4:999.1.1.1`, `ip4:1.2.3.4/33`, `ip6:zzz` skipped without error; a good term beside them is still kept.
  - A loop (`a.example` includes `b.example` includes `a.example`) terminates and returns what it found.
  - `redirect=` to itself terminates.
  - A record with 80 includes of distinct hosts performs at most 50 lookups and returns the ranges found before the cap.
  - A target with no SPF record contributes nothing and does not fail.
  - A lookup that throws `HttpRequestException` makes `ExpandAsync` return null.
  - Duplicate ranges from two routes are listed once per (`Include`, `Cidr`).
  - JSON round trip, and `Parse(null)`, `Parse("")` and `Parse("not json")` return empty.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** `SpfRangeExpander` as a depth-first walk with a path set and a lookup counter; `a`, `mx`, `ptr` and `exists` mechanisms are ignored in Phase 1 (they need address and MX lookups the app does not do for SPF, and they never name a service; the spec's own-server `a`/`mx` collection is deferred and recorded as a ruling in the ledger).
- [ ] **Step 4: Run the tests, expect PASS.**
- [ ] **Step 5: Commit** `Expand a domain's SPF record into tagged IP ranges`.

### Task 4: Store ranges when the SPF check runs

**Files:**
- Modify: `src/DotMarc/Data/Domain.cs` (add `public string? SpfRangesJson { get; set; }` beside the SPF fields), `src/DotMarc/Dns/SpfCheckResult.cs`, `src/DotMarc/Dns/SpfDnsChecker.cs`, `src/DotMarc/Ingestion/PollingService.cs:500`
- Create: one EF migration `AddSpfRanges` (nullable text column on Domains; `dotnet ef migrations add AddSpfRanges`, like the earlier sender migrations)
- Test: `test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs` (extend), `test/DotMarc.Tests/Ingestion/` (the existing `RunSingleSpfCheckAsync` tests; find them with grep)

**Interfaces:**
- Consumes: `SpfRangeExpander`, `SpfServiceRanges.Serialize`.
- Produces: `SpfCheckResult(SpfCheckStatus Status, string? Detail, IReadOnlyList<SpfServiceRange>? Ranges = null)`. `Ranges` is null when the ranges should be left as they were, an empty list when the domain authorises nothing (missing record, null SPF), otherwise the expansion. `SpfDnsChecker` takes `SpfRangeExpander` built from the same lookup it already uses. `RunSingleSpfCheckAsync` sets `domain.SpfRangesJson = SpfServiceRanges.Serialize(result.Ranges)` only when `result.Ranges` is not null.

- [ ] **Step 1: Write the failing tests:**
  - A domain whose record has `include:servers.mcsv.net` (fake lookup) returns `Ranges` with the Mailchimp ranges and status `Ok`.
  - Missing record and null SPF return an empty `Ranges`.
  - Multiple records, and an expansion whose lookup throws, return `Ranges` null.
  - `RunSingleSpfCheckAsync` with a result carrying ranges stores the JSON; with `Ranges` null leaves an existing `SpfRangesJson` untouched; with an empty list stores `[]`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** the property, the migration, the result member and the wiring. Keep the existing public constructor of `SpfDnsChecker` working (DI uses it) and build the expander from the same `TxtRecordLookup`.
- [ ] **Step 4: Run the DNS and polling tests, expect PASS. Confirm the migration applies in the Postgres fixture tests.**
- [ ] **Step 5: Commit** `Store the SPF ranges found by the daily SPF check`.

### Task 5: The SPF range signal

**Files:**
- Modify: `src/DotMarc/Reporting/Senders/SenderScoring.cs`
- Test: `test/DotMarc.Tests/Reporting/SenderScoringTests.cs` (extend)

**Interfaces:**
- Consumes: `SpfServiceRange`, `CidrRange`.
- Produces: `SenderSignal` gains `SpfRange`; `public const double SpfRangeWeight = 0.85;`; `SenderScoring.Score(SenderSourceInput source, IReadOnlyCollection<string> spfIncludedNames, IReadOnlyList<SpfServiceRange>? spfRanges = null)`. A range with a non-null `Service` equal to the catalogue service's name matches when its parsed `CidrRange` contains the source IP (an unparseable source IP matches nothing). Evidence text: `Your SPF record authorises this IP for {service.Name}`. When it matches, the `SpfInclude` signal for that service is not added.

- [ ] **Step 1: Write the failing tests:**
  - A source IP inside a Mailchimp-tagged range scores 0.85 alone for Mailchimp, with a `SpfRange` evidence line.
  - An IP outside the range scores nothing from it.
  - Range plus SPF include name for the same service scores 0.85, not `1 - 0.15*0.80`: the include signal is absent from the evidence list.
  - A range with `Service` null contributes nothing.
  - Range plus a passing DKIM auth domain combines as `1 - (1-0.90)(1-0.85)` and caps at 0.99.
  - An IP inside two services' ranges scores both; the order is by score then catalogue order.
  - `spfRanges` omitted or empty gives exactly today's scores (the existing tests pass unchanged).
  - An IPv6 source IP inside an `ip6:` range matches; a malformed source IP string does not throw.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** in `Score`: parse each range's `CidrRange` once per call (not per service) and skip unparseable ones.
- [ ] **Step 4: Run `SenderScoringTests`, expect PASS.**
- [ ] **Step 5: Commit** `Score a source by the SPF ranges its domain authorises`.

### Task 6: Pass the ranges to every caller

**Files:**
- Modify: `SenderIdentification.Identify` and `IdentifyRecords` (new optional last parameter `IReadOnlyList<SpfServiceRange>? spfRanges = null`, passed to `Score`), `SenderDecisionSummaries.Summarise`, `SenderAlertInputs` and `SenderAlertEvaluator`, `AlertingService` (line ~171), `ClientReportCalculator` and `ClientReportInputs`/`ClientReportBuilder`, `PortalData` (line ~74), `SourceEndpoints` (line ~143), `DomainDetail.razor` (`RebuildSenders`, line ~623).
- Test: `test/DotMarc.Tests/Reporting/SenderIdentificationTests.cs`, `SenderDecisionSummaryTests.cs`, `ClientReportCalculatorTests.cs`, `test/DotMarc.Tests/Api/SourceEndpointTests.cs`

**Interfaces:**
- Consumes: `Domain.SpfRangesJson`, `SpfServiceRanges.Parse`, the Task 5 `Score` signature.
- Produces: every caller above passes `SpfServiceRanges.Parse(domain.SpfRangesJson)` for the domain being analysed. Where a caller works over many domains (client report, alerts) it uses each domain's own ranges. Callers that currently pass `[]` for the include names keep doing so; this phase does not change that.

- [ ] **Step 1: Write the failing tests:**
  - `SenderIdentification.Identify` with a range tagged `SendGrid` containing an otherwise unrecognised IP puts that IP in the SendGrid row, with the evidence line.
  - `SenderDecisionSummaries.Summarise` given the ranges counts that IP under the service decision (a service-level Unauthorised decision now blocks and counts its mail; unreviewed count drops from an unmatched IP to the service).
  - A client report calculation for a domain with ranges attributes the same way (use the existing unauthorised-sentence test setup).
  - A sources API test: a domain with `SpfRangesJson` set seeds a source IP inside a tagged range and the `service` field is that service, `decision` follows a service decision.
  - A domain with `SpfRangesJson` null behaves as before in each of these.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** the optional parameters and the plumbing at each caller. The Overview count and the Sources tab share `_senderResult`, so they update together. Load the domain's `SpfRangesJson` wherever the domain entity is already loaded; add it to a projection only where one is used.
- [ ] **Step 4: Run the full suite** (redirect output to a file, read the tail), expect all green.
- [ ] **Step 5: Commit** `Use SPF ranges when naming senders everywhere`.

### Task 7: Demo and docs

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataGenerator.cs` or `DemoDataset.cs` and `DemoDataSeeder.cs` (set `SpfRangesJson` for the demo domain `shop.aurora-retail.example`: a range tagged `SendGrid` that contains one of its currently unmatched IPs; read the generator to pick the IP), the docs page that describes the Senders table (find it: grep `website/docs` for "Unrecognised")
- Test: `test/DotMarc.Tests/Demo/` (the existing demo seeding tests: a domain with ranges is seeded and that IP is attributed)

**Interfaces:**
- Consumes: `SpfServiceRanges.Serialize`, `SpfServiceRange`.

- [ ] **Step 1: Write the failing demo test:** seeding the demo gives `shop.aurora-retail.example` a non-empty `SpfRangesJson`, and `Identify` over its reports names the chosen IP's service with an `authorises this IP` evidence line.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** the seed data, then add a short paragraph to the Senders docs: the SPF record's IP ranges are read when the daily SPF check runs, a source inside a range your record authorises for a service is named after that service, and the evidence says "authorises", not "confirms". No em dashes.
- [ ] **Step 4: Run the demo tests, then the full suite; expect all green.**
- [ ] **Step 5: Browser check** with the recipe in the session notes (docker postgres on 55432, Development environment, `Demo__Enabled=true`, Edge via playwright-edge): shop.aurora's Sources tab shows the IP under its service, the evidence line reads "Your SPF record authorises this IP for ...", and the Overview "senders need a decision" count drops by one. Stop the app and container afterwards.
- [ ] **Step 6: Commit** `Show SPF range matching in the demo and docs`.
