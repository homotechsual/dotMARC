# Bulk DNS push, per provider (0.14 candidate)

A candidate for 0.14, written up for review. Nothing here is built. It is the largest of the 0.14 candidates.

## Intent

An MSP with fifty client domains on Cloudflare shouldn't have to open fifty domain pages, click Push fifty times and sign in to Cloudflare fifty times to roll out the same record. Pick the domains, pick the record, sign in to the provider once, check what will change for each, and push.

## What happens today

A push is one domain, one target, one provider, one sign-in.

* **Start.** The domain page opens a popup at `/dns-push/{provider}/start?domainId=&target=`. The endpoint checks the request came from dotMARC's own page (`DnsPushStartGuard`), that the person holds the target's policy (`IDnsChangeBuilder.RequiredPolicy`) and that the provider's OAuth app is configured, then redirects to the provider with PKCE.
* **State.** What the callback needs travels as the OAuth `state` parameter, as an opaque Data Protection token (`DnsPushStateProtector`, five minute lifetime) holding the domain id, target, code verifier and optional payload. There is no server-side session.
* **Callback.** `/dns-push/{provider}/callback` re-checks the policy, loads the domain, detects its zone again (so a domain that moved DNS provider since the page loaded is refused, not pushed to the wrong place), and builds the plan again at push time. It then calls `IDnsPushProvider.ExchangeAndPushAsync`, which exchanges the single-use authorization code once and pushes the ordered list of changes against that exchange, stopping at the first change that doesn't succeed, with nothing rolled back.
* **Result.** One audit entry (`DnsPushAudit`), and the popup posts a coarse result flag to the pushing page over a `BroadcastChannel`.
* **Review.** Since 0.12.0 every push shows a review first (`DnsPushReview`), built from the same plan: additions, removals and lost CNAME delegations. The review and the push derive the plan the same way, so what you see is what gets written.
* **Targets.** `dmarc`, `dmarc-auth`, `tlsrpt`, `mta-sts`, `dkim` and `spf`, one `IDnsChangeBuilder` each. A change carries its own `ZoneName`.
* **Providers.** Cloudflare, Azure DNS and Google Cloud DNS (OAuth). A domain's provider and zone are already detected and stored (`Domain.DnsProvider`, `DnsZone`).

## Goals

* Select several domains, choose a target, and push to all the domains that are on one provider with one sign-in.
* A review for the whole batch that still shows, per domain, exactly what will be added, removed or lose a delegation, and lets someone drop domains before pushing.
* A per-domain result afterwards, and an audit entry per domain as today.
* Never push something the person did not review.

## Non-goals

* Targets whose content is per domain and typed by a person: `spf` (the edited record) and `dkim` (the record values) stay one at a time.
* Scheduling, automatic pushing, or pushing from the API.
* Rolling a batch back. The audit entries keep what each record was, which is what a manual revert needs.
* Pushing to several providers with one click. A selection that spans providers becomes one batch per provider, each with its own sign-in.

## Which targets

* **In the first version:** `dmarc`, `tlsrpt` and `mta-sts`. Each builds its plan from the domain's own data and live DNS, with no per-domain typing.
* **A special case, later:** `dmarc-auth` writes one record per domain into the mailbox's domain's zone, so it is the most bulk-friendly (many records, one zone, one sign-in) but is grouped by the mailbox domain's provider, not each domain's.

## Design

1. **Select and group.** On the Dashboard (and Manage domains), checkboxes and a **Push DNS records** action for people with the target's policy. The selection is grouped by each domain's detected provider. A provider with no configured option, and domains whose provider isn't detected, are listed as "set up manually" with the existing copy-table flow, and are not part of a batch.
2. **Build and review before sign-in.** For each selected domain the plan is built with the existing builder and shown with the existing review layout, grouped per domain, with a summary ("38 will change, 9 already correct, 3 need attention"). Domains with nothing to change are ticked off automatically, refusals and lost delegations are called out, and each domain can be unticked. A batch has a size limit (a starting value of 50).
3. **Server-side batch.** A batch of domain ids will not fit in an OAuth `state` parameter, so a `DnsPushBatch` record is stored (id, who started it, provider, target, the domains and a fingerprint of each reviewed plan, created, expires, used) and the `state` token carries only the batch id and the PKCE verifier. It lives in the database, not in memory, because dotMARC can run as several replicas. It is single use and short lived.
4. **Callback runs the batch.** It validates the state and the policy, loads the batch, exchanges the code once, and then for each domain: detects the zone again, builds the plan again, and compares its fingerprint with the reviewed one. A different plan is **skipped and reported** ("changed since you reviewed it"), never pushed. A matching one is pushed, with its own result and audit entry, and a failure in one domain does not stop the others.
5. **Split the provider call.** `ExchangeAndPushAsync` does the token exchange and the push in one call and stops at the first failure. The batch needs the exchange once and a push per domain. Add `ExchangeAsync` returning a session and `PushAsync(session, changes)`, and keep `ExchangeAndPushAsync` as the composition so single pushes are unchanged. Each provider must be checked to confirm one token can serve several zones: Cloudflare's token scope, Azure's subscription and resource group scope, and Google's project.
6. **Run it in the background.** Fifty domains of API calls can outlast a request and a reverse proxy's timeout. The callback exchanges the code, closes the popup, and hands the session to a background worker on that instance. The session token stays in memory only and is dropped when the batch finishes, because it is a bearer credential to the customer's DNS. Progress and per-domain results are stored on the batch, so the page can show them (polling the batch, or through the circuit) whichever replica serves it.
7. **Result.** A table of domains with Pushed, Already correct, Skipped (changed since review), Zone not found, or Failed with the reason. Retrying the failed ones starts a new batch and a new sign-in, since the code is single use.
8. **Audit.** A batch-level entry (who, provider, target, how many domains) plus one `DnsPushAudit` entry per domain, exactly as a single push records it, including the previous values.
9. **Rate limits.** Provider APIs limit requests, so domains are pushed one after another with a small pause and retry-after handling, and the progress view says so when it waits.

## Left to the builder

The review's layout for many domains (collapsed per domain with a summary line is the likely shape), polling against push for progress, the batch size limit, and how the background worker is hosted and shut down cleanly.

## Anti-goals

Pushing a record that was not reviewed. One domain's failure hiding the others' results. A session token written to disk or the database. A batch id that can be replayed. Hitting a provider's rate limit hard enough to lock the account.

## Test plan

* Pure: grouping a selection by provider, the plan fingerprint (equal plans match, a changed value or removal does not), the size limit.
* A fake provider with token reuse: all domains pushed after one exchange, one domain failing while the rest succeed, one plan changed since review being skipped, zone not found for a domain that moved provider, the batch id refusing a second use and an expired one.
* Authorization: the target's policy is required at start and at callback, and a batch started by one person is refused for another.
* Audit: one entry per domain and one for the batch, with previous values.
* Browser check on the demo, if the demo has a fake provider: select domains, review, push, see per-domain results. If it has none, add a demo provider first.

## Settled in planning (my calls, to be corrected if wrong)

* The batch size limit starts at 50.
* `dmarc-auth` is not in the first version.
* Failed domains are retried with a fresh sign-in. The provider session is never kept past the batch.
* A domain that would lose a CNAME delegation is unticked by default in the review and needs an explicit tick.
* The demo has no DNS push provider (only Cloudflare, Azure DNS and Google Cloud DNS are registered), so a fake demo provider is part of this work, in the same way as `DemoPsaProvider`.

## Open questions

* Does one token serve several zones on each of the three providers? Slice C answers this against real test zones. Until then the design assumes it does and keeps the single-domain path untouched.
