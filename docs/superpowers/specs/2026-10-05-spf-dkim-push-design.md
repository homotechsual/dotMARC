# SPF and DKIM push: edit SPF and publish DKIM records from dotMARC

Date: 2026-10-05. Status: approved design, not yet planned or built. Target release: v0.8.0.

## Problem

DNS provider push can create or repair the DMARC, DMARC authorization, TLS-RPT and MTA-STS records on Cloudflare,
Azure DNS and Google Cloud DNS. SPF and DKIM problems are detected and shown on the domain health checklist (and,
since the DNS health alerts, alert), but still have to be fixed by hand in the DNS provider's console. They're among
the most common reasons DMARC fails. This is the "Extend DNS Provider push to cover SPF and DKIM record pushes" idea
on the roadmap.

## Decisions

1. **SPF gets an editor, not a single fix button.** It can create a record, add and remove includes, merge several
   SPF records into one, and set the `all` ending. An admin setting chooses `~all` or `-all` for new records.
2. **The 10-DNS-lookup limit is enforced.** A record over 10 lookups can't be pushed, unless the live record is
   already over 10 and the edit lowers the count.
3. **The SPF health check counts lookups too**, with a new "Too many DNS lookups" status, and an include that doesn't
   resolve marks the record misconfigured.
4. **DKIM records are pasted once and kept.** Each selector can have an expected record (CNAME target or TXT key),
   pre-filled where predictable. dotMARC pushes it, and the DKIM check compares DNS with it.
5. **The edited SPF record travels in the encrypted push state**, with a fingerprint of the live record it was built
   from. The callback refuses if the live record changed in between.
6. **At the domain's apex only the SPF value changes.** Other TXT values there (site verifications and the like) are
   never touched.
7. **The push callback is split into one change-builder per target** before the two new targets are added.

## SPF

### Parsing

`SpfRecord` parses a TXT value starting with `v=spf1` into terms, each a mechanism (`all`, `include`, `a`, `mx`,
`ptr`, `ip4`, `ip6`, `exists`) with an optional qualifier (`+`, `-`, `~`, `?`) and value, or a modifier (`redirect=`,
`exp=`, or an unknown `name=value`). Anything it doesn't recognise is kept as an opaque term. Formatting a parsed
record reproduces the terms in order, single-space separated, so an edit changes only what it means to change.
Mechanism names compare case-insensitively.

### Counting lookups

`SpfLookupCounter` counts DNS lookups as RFC 7208 §4.6.4 does: each `include`, `a`, `mx`, `ptr`, `exists` and
`redirect` costs one, and `include` and `redirect` are followed recursively (their target's SPF record is fetched over
the same DNS-over-HTTPS resolver the checks use, and its terms counted too). It returns:

- the total;
- the cost of each top-level term (an include's cost is 1 plus everything under it);
- problems: an include or redirect target with no SPF record, a loop (a domain already being followed), more than 2
  lookups that return nothing ("void lookups", RFC 7208 §4.6.4), and stopping after following 10 levels deep.

Counting stops once it passes 20, since the exact figure no longer matters.

### The check

`SpfDnsChecker` keeps its existing results and, for a single valid `v=spf1` record, then counts lookups:

- over 10: new status `SpfCheckStatus.TooManyLookups`, detail naming the count and the three costliest includes;
- an include or redirect with no SPF record: `Misconfigured`, detail naming it;
- otherwise as today (`Ok`, or `NullSpf` for `v=spf1 -all` alone).

A lookup that fails (network error) leaves the check as it would have been without counting, rather than inventing a
problem. The DNS health alert treats `TooManyLookups` as failing, like any other SPF failure.

### The editor

An **Edit SPF** action on the SPF row of the domain health checklist (needs `DomainsEdit`) opens the editor. Its
starting point:

- **One live SPF record:** that record.
- **Several:** one record merged from them: their terms in order of appearance, duplicates removed, one `all` term
  (the strictest present: `-` over `~` over `?` over `+`), and, if an `all` is present, any `redirect=` turned into an `include:` of the same domain (receivers ignore a redirect alongside `all`, so dropping it would de-authorise its senders). Only the first of each modifier is kept. The editor
  says it's a merge of N records.
- **None:** `v=spf1 include:<service> <default ending>` for each detected sending or inbox service that has a known
  include, or just `v=spf1 <default ending>` with a prompt to add an include. A **This domain sends no mail** option
  sets `v=spf1 -all`.

What it can change:

- **Remove an include** (any `include:` term).
- **Add an include**, from a list of known services (Microsoft 365 `spf.protection.outlook.com`, Google Workspace
  `_spf.google.com`, Zoho `zoho.com`/`zoho.eu`, Mailchimp `servers.mcsv.net`, SendGrid `sendgrid.net`, Amazon SES
  `amazonses.com`, Salesforce `_spf.salesforce.com`), with the detected services suggested first, or typed in (must
  be a valid host name).
- **The ending:** `-all` (fail) or `~all` (softfail). A live `+all` or `?all` is shown with a warning that it
  doesn't protect the domain, and is replaced if the person picks one of the two.

Every other term is kept as it is. New includes are added before the `all` term.

As the person edits, the editor shows the resulting record, its total lookup count, each include's cost and any
problems, and its length. A result over 450 characters shows a warning (some resolvers truncate large TXT
answers).

**Blocking:** **Push** is disabled, with the reason, when the result would need more than 10 lookups, unless the live
record (or, for a merge, the worst live record) already needs more than 10 and the result needs fewer. It's also
disabled when the result is the same as the live record.

### The ending setting

A new singleton settings row, `DnsRecordSettings`, with `SpfAllQualifier` (`SoftFail` for `~all`, the default, or
`Fail` for `-all`), edited on **DNS push settings** (`/dns-push/settings`, which needs the existing permission for that
page) and audited as `settings.dns_records.saved` ("DNS record settings saved"). It's the ending for new records and
the editor's starting ending for a merge with no `all`.

## DKIM

### Stored records

A new table, `DomainDkimRecords`: `DomainId`, `Selector`, `RecordType` (`Cname` or `Txt`), `Value`. Unique on
(`DomainId`, `Selector`), cascade-deleted with the domain. A record exists only for a selector in the domain's
`DkimSelectors`; removing a selector removes its record.

### Entering them

The DKIM selectors dialog gets, under each selector, a record type (CNAME or TXT) and a value box, saved with the
selectors.

- **Pre-filled** for Fastmail selectors (`fm1`, `fm2`, `fm3`): CNAME `fmN.<domain>.dkim.fmhosted.com`.
- **Help text** for where to find the values: Microsoft 365 (the Defender portal's DKIM page, or
  `Get-DkimSigningConfig`'s `Selector1CNAME` and `Selector2CNAME`), Google Workspace (Admin console › Apps › Google
  Workspace › Gmail › Authenticate email), Zoho Mail and Proton Mail (their domain DKIM settings).
- **Tidying a paste:** surrounding quotes, line breaks and the `"..." "..."` chunking some consoles show are removed,
  and runs of spaces collapsed. A CNAME value loses a trailing dot.
- **Validation:** a CNAME value must be a valid host name; a TXT value must contain a `p=` tag with a non-empty value.

Saving is audited as `domain.dkim_records_changed` ("Domain DKIM records changed") with each selector's record before
and after. The values are public (host names and public keys), so they're recorded in full.

### The check

`DkimDnsChecker` takes each selector's expected record, if any:

- **CNAME expected:** it looks up the CNAME at `<selector>._domainkey.<domain>`. None: `Missing`. A different
  target (compared ignoring case and a trailing dot): `Misconfigured`, "selector1 points to X, expected Y". The right
  target but no TXT key at the end of it: `Missing`, "the CNAME is in place, but the mail platform hasn't published
  the key yet: turn on DKIM signing there".
- **TXT expected:** no record: `Missing`. A record whose `p=` value differs from the expected one: `Misconfigured`.
- **No expected record:** as today (a TXT record with `p=` passes).

## Pushing

### Change-builders

The `/dns-push/{provider}/callback` handler's per-target if/else becomes one `IDnsChangeBuilder` per target:
`mta-sts`, `dmarc`, `dmarc-auth`, `tlsrpt`, `spf`, `dkim`. Each declares the policy it needs (`MtaStsManage` for
`mta-sts`, `DomainsEdit` for the rest), whether it writes to the domain's own zone or the mailbox domain's
(`dmarc-auth` only), and builds the list of changes or a refusal flag. `/start` and the callback both look the
builder up by target, so the permission check lives in one place. The four existing targets keep their exact
behaviour.

### State

`DnsPushState` gains an optional `Payload` string, protected and expiring like the rest of the state. Only `spf` uses
it: the proposed record and the fingerprint (SHA-256 of the live SPF values, sorted and joined) of what the editor
started from.

### SPF changes

The `spf` builder re-reads the live SPF record(s) at the apex and:

- refuses with `spf-changed` if their fingerprint differs from the payload's;
- re-counts the proposed record's lookups and refuses with `spf-too-many-lookups` under the blocking rule above;
- refuses with `nothing-to-push` if the proposed record equals the single live record;
- otherwise builds one change of a new kind, **replace values in a TXT set**: at the apex, remove the listed live SPF
  values and add the proposed one.

Each provider implements that kind without touching any other value at the name:

- **Cloudflare:** delete the records whose content is one of the listed values, then create the new record.
- **Azure DNS:** read the apex TXT record set, remove the listed values, add the new one, write the set back.
- **Google Cloud DNS:** read the apex TXT rrset, and submit one change that deletes it and adds it back with the
  listed values removed and the new one added.

### DKIM changes

The `dkim` builder reads the domain's stored records and, for each selector whose live record doesn't match, builds:

- **create** when nothing exists at the name;
- **update** when the same type exists with a different value;
- **replace** when the other type exists (CNAME to TXT, or TXT to CNAME).

It refuses with `nothing-to-push` when everything matches. The domain page shows **Push DKIM records** when the DKIM
check isn't passing and at least one selector has a stored record, and confirms first (listing old and new values)
whenever something would be overwritten, as today's pushes do.

### Provider changes

- **Long TXT values** are split into 255-character strings in the form each provider expects.
- **CNAME create and update** go through the same paths the MTA-STS push already uses.
- **Replace** works in both directions (today only CNAME to TXT).

### Popup outcomes

The popup's result flags gain `spf-changed` ("SPF changed since you opened the editor. Reopen it to start from the
current record."), `spf-too-many-lookups` ("This record would need more than 10 DNS lookups, so it wasn't pushed.")
and `nothing-to-push` ("DNS already matches, so there was nothing to push.").

### Audit

Every push is already audited by `DnsPushAudit` with each change's old and new values, so SPF and DKIM pushes are
covered. The new change kind is described as "replaced SPF value X with Y".

## Demo

Demo domains use `.example` names with no real DNS, so the SPF editor opens on "no record" and its lookup counter
finds nothing; the demo has no DNS provider accounts, so nothing can be pushed. The DKIM records dialog works as
usual.

## Docs

- `website/docs/dns-provider-push.mdx`: sections on the SPF editor (merging, includes, ending, the lookup limit,
  apex safety, the setting) and DKIM records (entering them, where to find them per platform, the check, pushing).
- The domain health checklist docs: the new SPF status and the DKIM comparisons.
- The roadmap idea "Extend DNS Provider push to cover SPF and DKIM record pushes" is marked complete.

## Testing

- **Pure**: SPF parsing and formatting (order, qualifiers, unknown terms, case); the lookup counter against a fake
  resolver (nested includes, redirect, loops, void lookups, depth, the stop at 20); merging several records; the
  editor's starting point and blocking rule; tidying and validating DKIM pastes; the DKIM check's comparisons.
- **Change-builders**: each of the six, including tests pinning the four existing targets' behaviour.
- **Providers, against fake HTTP**: apex TXT set editing keeps other values; long TXT splitting; CNAME create and
  update; replace in both directions.
- **Database**: saving DKIM records (and removal with their selector); the new settings row and its audit; the SPF
  check's new status through the check cycle.
- **Browser check in the demo**: the SPF editor (preview, include costs, blocking), the setting, and the DKIM records
  dialog.

## Out of scope

- SPF flattening (replacing includes with their IP addresses).
- Editing `ip4:`/`ip6:`/`a`/`mx` terms in the editor (they're kept as they are).
- Generating DKIM keys, or fetching them from Microsoft 365 or Google Workspace APIs.
- Pushing to DNS providers other than the three supported today.
