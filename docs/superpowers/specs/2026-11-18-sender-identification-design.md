# Identify the sending services behind a domain's DMARC reports

## Goal

For each domain, show which sending services (Microsoft 365, Google Workspace, Mailchimp and so on) its DMARC reports point to, how sure we are of each, how much mail each sends, and which of them are failing authentication and why. Today a source is a bare IP and only failing sources get a provider name, buried in one recommendation sentence.

It is informational. It never changes DNS or sends anything.

## Why

An MSP reading a domain's reports wants to answer, quickly:

- Who is sending as this domain? (an inventory, including services nobody mentioned)
- Which of those are failing, and what is the fix for each? (triage per service, not per IP)
- Is there mail from something we can't name? (possible spoofing or a forgotten tool)

## Goals

- One row per service for the domain over the 30-day window: message volume, share of total, evidence strength, pass rate, failing volume and the failure cause.
- Evidence strength shown next to a plain-language list of what it is based on, so a number is never unexplained.
- Failures grouped by service, with a fix that names the service.
- Sources we can't attribute are listed as unrecognised, never guessed.
- The same data feeds the failure recommendations and the client report conclusions.

## Non-goals

- No database change and no new settings. The result is computed when the page loads.
- No cross-domain views ("which of my domains use Mailchimp"), no Dashboard filter and no new alerts. Those need the attribution stored on each record, and are a follow-up if wanted.
- No exact SPF include or DKIM selector values to add. They vary by account and go stale. We name the service and the mechanism to fix, as `DomainStatistics.KnownEmailServiceProviders` already does.
- No new outbound lookups. We use only the report data, the cached RDAP results and the domain's already detected mail services.

## Anti-goals

- A confident-looking wrong answer. If the evidence is thin, the row says so and the confidence is low.
- A percentage presented as a probability. It is a score of evidence, and the page says "evidence strength".

## Approaches considered

- **A. Compute when the page loads (chosen).** A catalogue in code, a pure scoring function, no migration. Catalogue improvements apply to old reports immediately. Can't query across domains in SQL.
- **B. Store the attribution on each record at ingest.** Cheap cross-domain queries and alerts. Needs a migration and a backfill, and each catalogue change needs another backfill.
- **C. A now, B later.** The scoring function is the same either way, so choosing A first loses nothing.

## Design

### The catalogue

`SenderServiceCatalog`, a static list in `src/DotMarc/Reporting/`, one entry per service:

- `Name` (matching the names `MailServiceDetector` and `SpfIncludeCatalog` already use, so they join up).
- `AuthDomainSuffixes`: SPF and DKIM domains that identify it (`.mcsv.net`, `.sendgrid.net`, `protection.outlook.com`, `.google.com`, `.amazonses.com`).
- `DkimSelectorPatterns`: selector shapes that identify it (Microsoft 365 `selector1`/`selector2`, Google `google`, Mailchimp `k1`/`k2`/`k3`).
- `IpOwnerPatterns`: words in the RDAP organisation name (`Microsoft`, `Google`, `Rocket Science`, `SendGrid`, `Amazon`).
- `FailureAdvice`: one line per failure cause saying what to fix, naming the service.

It starts from the 13 services already in `KnownEmailServiceProviders` (SendGrid, Mailgun, Mailchimp, Mandrill, Salesforce, Salesforce Marketing Cloud, Zendesk, HubSpot, Freshdesk, Constant Contact, SparkPost, Postmark, Amazon SES), plus Microsoft 365, Google Workspace, Zoho, Fastmail and Proton Mail from `MailServiceDetector`.

### Scoring

For one source IP, each matching signal adds evidence. Weights are fixed constants in one place so they are easy to tune:

| Signal | Weight |
|---|---|
| An SPF or DKIM domain in the record's auth results matches the service, and that check passed | 0.90 |
| An SPF or DKIM domain matches, and the check failed | 0.80 |
| The DKIM selector matches the service's pattern | 0.60 |
| The IP's RDAP organisation matches | 0.50 |
| The domain's own SPF record includes the service (from the existing mail service detection) | 0.20 |

Evidence for one source combines as `1 - product(1 - weight)` over its distinct signals (each signal type counted once), so two independent signals reinforce each other and none can reach 100%. The score is capped at 99%.

For the domain, a service's evidence strength is the highest score across its sources. Its volume is the sum of the messages from sources attributed to it. A source is attributed to the service with the highest score for it, if that score is at least 30%. Below that it goes to **Unrecognised**.

Confidence bands for sorting and colour: 70% and above is strong, 40 to 69% is moderate, below 40% is weak.

### Failures per service

For each service, over its attributed records:

- Messages that failed DMARC (neither SPF nor DKIM passed in alignment), split by cause using the existing `ClassifyMechanism` and `ClassifyFailureCategory`: SPF not aligned, DKIM not aligned, DKIM missing, both.
- The service's `FailureAdvice` for the dominant cause.
- Messages the receiver disposed against (quarantine or reject), kept separate from messages that merely failed.

### Where it appears

- **Domain page, Sources tab:** a **Senders** table above the existing per-IP table. Columns: Service, Messages, Share, Evidence, Pass rate, Failing, Fix. Expanding a row shows the evidence list ("DKIM d=mcsv.net passed; IP owned by The Rocket Science Group") and its source IPs. **Unrecognised** is the last row, with its top IPs and owners. The per-IP table stays, and gains a Service column.
- **Failure recommendations:** `BuildRecommendation` uses the attributed service instead of its own suffix list. `IdentifyKnownEsp` is removed, so there is one list.
- **Client report:** the conclusions name the failing services ("Mailchimp is sending 4,200 messages a month that fail DKIM alignment").
- **Portal and API:** top sources include the service name where known. The API field is added to the existing source objects, so it is not a breaking change.

### Data flow

`Reports` and `ReportRecords` with `AuthDetails` (already loaded for the Sources tab) and `IpInfo` and `IpRange` from `IpInfoService` go into `SenderIdentification.Identify(...)`, which returns the service rows and the unrecognised row. It does no I/O, so it is unit tested directly.

## Constraints

- The Sources tab must not slow noticeably. It works on records already in memory, so the cost is a pass over them.
- Names must match the existing ones so the Dashboard, domain detection and this feature agree.
- Existing copy rules apply: no em dashes, and failure text says the cause first, then the fix.

## Testing

- Table-driven tests for the scoring: each signal alone, combined signals, the 99% cap, the 30% attribution threshold, a source matching two services, a source matching none.
- Failure grouping per service, including a service with passing and failing sources.
- The recommendation text names the attributed service.
- A real-shaped sample for each of Microsoft 365, Google Workspace and Mailchimp built from typical report records.
- A component test that the Senders table renders and **Unrecognised** appears last.

## What's left to the builder

- Final weights, if the first real reports show a weight is off. Keep them in one place.
- Exact column order, widths and colours, following the Dashboard's existing tables.
- The copy for each service's `FailureAdvice`.
- Whether the client report shows the top three failing services or all of them.

## My calls (to check)

- Confidence is evidence strength with a 99% cap, so nothing says 100%. You gave 100% as an example.
- 30% is the attribution threshold.
- A-then-B: no persistence now.
- The weights above are my starting guesses, not measured.
