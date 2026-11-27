# DKIM selector detection from reports (0.14 candidate)

A candidate for 0.14, written up for review. Nothing here is built.

## Intent

The DKIM check in dotMARC only runs once someone has typed in the selectors to check. The reports dotMARC already receives say which selectors are really signing a domain's mail. The selectors should come from that evidence, so setting up and keeping the DKIM check is one click, and a signing selector whose key disappears from DNS is caught.

## What happens today

* **The check is opt-in.** With no selectors configured, a domain's DKIM status is `NotConfigured`, the check is skipped and it never alerts (`PollingService`, `Domain.DkimSelectors`). The comment on `DkimCheckStatus` says dotMARC has no way to discover selectors on its own, which is no longer true.
* **Suggestions are a pre-fill.** `ConfigureDkimSelectorsDialog` pre-fills the box only for a domain with no selectors whose MX points at one of five inbox providers (Microsoft 365: `selector1`, `selector2`; Google Workspace: `google`; Zoho Mail; Fastmail; ProtonMail), from `DkimSelectorSuggestions`. A person has to open the dialog and save. Sending services get nothing, because their setup is per account.
* **Reports carry the real selectors.** Every DKIM signature in a report's `auth_results` is stored as a `ReportRecordAuthDetail` with the signing domain (`d=`), the selector, the result and the message count of its record. The selector is used as evidence on the Senders table (`SenderScoring`) and nowhere else.
* **`google` is only Google's default.** An admin can choose another prefix when generating the key, and only the reports show which one actually signs.

## Goals

* Show, per domain, the selectors seen signing its own mail, with message counts and when each was last seen.
* Add one with one click, in the existing Configure DKIM selectors dialog, for any mail platform and not only the five inbox providers.
* Optionally keep the checked selectors in step with what is seen, and alert when a selector that was signing no longer has a key in DNS.

## Non-goals

* Guessing selectors by probing DNS with a list of common names.
* Checking the selectors of third-party services that sign as their own domain. Those keys live in the service's DNS, not the customer's.
* Pushing records to DNS for a detected selector. The existing DKIM push flow stays as it is.

## What counts as a detected selector

A DKIM auth detail counts for a monitored domain when:

* its signing domain (`d=`) is that domain, because the key is published at `<selector>._domainkey.<d=>`. A signature whose `d=` is another monitored domain counts for that domain instead. One whose `d=` is not monitored is ignored.
* its result is pass, on at least a minimum number of messages in the last 30 days (a starting value of 10, to keep one-off forwarding and test mail out), counting messages, not reports or records.

A selector delegated to a service by CNAME, such as Mailchimp's `k2` or a Microsoft 365 selector, signs as the customer's domain, so it is detected and the check follows the CNAME as the existing DKIM checker already does.

## Approaches

1. **Suggest only (recommended first step).** The dialog lists detected selectors that are not yet configured, each with its message count and last seen date, and an Add button. No schema change: it is a query over the 30 days of auth details, written as a pure function so it can be tested like the other detectors. The pre-fill by MX stays as the fallback for a domain with no reports yet.
2. **Maintain automatically, behind a setting.** A "Check selectors seen in reports" switch (per domain, with a global default) adds detected selectors to the checked set and drops ones not seen for N days. This needs a small table of observations (`DomainId`, `Selector`, `FirstSeenUtc`, `LastSeenUtc`) so a selector that rotated away is pruned after a while and not forgotten at once.
3. **Apply detected selectors straight into `DkimSelectors`.** Simplest, but it silently rewrites what an admin typed and can't undo a pruning decision. Rejected.

The alert in approach 2 is the valuable part: "this selector was signing within 14 days and its key is now missing or broken" is a precise failure signal, much better than the current alert for a hand-typed selector. It must not fire for a selector that stopped signing, because Microsoft 365 rotates between `selector1` and `selector2` and the old one can legitimately go.

## Design (approach 1, with room for 2)

* A pure `DkimSelectorDetector` takes a domain's auth details (signing domain, selector, result, message count, report time) and the domain name, and returns detected selectors with messages and last seen. It is the part approach 2 reuses.
* `ConfigureDkimSelectorsDialog` gets a **Seen in your reports** list above the text box, which it loads when it opens. Add puts the selector in the box. The existing flow then takes it (pre-fills the record from DNS, saves).
* The domain Overview's DKIM row says when selectors are seen but not configured, with a Configure button that opens the dialog, so the gap is visible without opening the dialog.
* Update the stale comment on `DkimCheckStatus`, and the docs ("DKIM selectors") to say reports are the source of suggestions.
* The API's domain DKIM block gets `selectorsSeen` (read-only), optional.

## Left to the builder

The minimum message count, whether the list also shows selectors already configured (with a tick), wording, and how the dialog loads the list without slowing its opening.

## Anti-goals

A DKIM alert for a selector that nobody uses. A rotated selector raising an alert the day after the rotation. Silently changing what an admin configured.

## Test plan

* Pure detector tests: own-domain signature counted, another domain's ignored, failed signatures ignored, below the minimum ignored, messages counted not records, a parent signing for a monitored child, two selectors, no reports.
* For approach 2: observation pruning at the boundary, a rotated selector not alerting, a signing selector going missing alerting once.
* Browser check on the demo: the dialog lists the demo's selectors, Add and Save make the DKIM status turn from not configured to checked.

## Open questions

* Should approach 2 be on by default for new installs, or always opt-in?
* Should detection also run on subdomains' mail that is signed by the parent domain's key (the parent is where the key lives)?
* Is 14 days the right memory for a selector that stopped signing?
