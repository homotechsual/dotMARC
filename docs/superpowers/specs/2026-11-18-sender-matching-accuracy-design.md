# Match sending services more accurately

Builds on `2026-11-18-sender-identification-design.md`. That feature scores a source by the SPF or DKIM domain in the report, the DKIM selector, the IP owner's name and the domain's SPF include names. This spec adds stronger evidence from public data, so more sources are named and fewer are guesses.

## Goal

Raise how many sources get a correct service, and how sure the evidence is, by using three kinds of public data:

1. The domain's own SPF record, expanded to the IP ranges it authorises for each service.
2. Reverse DNS of the source IP.
3. IP ranges that the big providers publish themselves.

It changes only how a source is scored. The Senders table, decisions, alerts and reports use the better result with no change of their own.

## Why

Today:

- A sender that signs with its own domain (Mailchimp, SendGrid and similar) is matched strongly.
- A properly set up Microsoft 365 or Google Workspace sender signs as the customer's own domain, so it is matched only by the IP owner's name from RDAP and a DKIM selector. That is a free-text name, and it misses smaller services altogether.
- The SPF include check only knows a service's name, and says nothing about whether this IP is one of its servers.

## What we checked in the documentation

From Microsoft Learn, Google's help and AWS documentation on 18 November 2026:

- **Microsoft 365** publishes its IP ranges and URLs through a web service at `endpoints.office.com`. A version endpoint reports the latest data version and should be checked no more than once an hour. The data endpoint returns JSON by default and can return HTTP 429 if called too often, so it is fetched only when the version changes. Exchange Online entries carry CIDR ranges.
- **Google Workspace** publishes its outbound ranges through the `_spf.google.com` record and the records it points to. Google's help says that record is complete for SPF but does not include every range used by other Google services, and points to a separate JSON file for those. The file's location was not confirmed. Third-party sources say the chain changed in 2025, so expansion has to follow the live DNS and not a hard-coded list.
- **AWS** publishes `ip-ranges.json`, with a service code per range. We could not confirm there is a code just for Amazon SES, so the builder checks the live file before relying on one. AWS itself advises against using the list for anything mission critical.
- There is no free, maintained database of "which IPs belong to which email service". The commercial DMARC vendors keep theirs proprietary.

## Goals

- A source whose IP falls inside a range that the domain's own SPF record authorises for a service is attributed to that service with strong evidence.
- A source whose IP reverse-resolves to a name that resolves back to the same IP, under a domain the catalogue ties to a service, gets moderate evidence.
- A source inside a provider's published ranges is attributed to that provider more reliably than by the RDAP organisation name.
- Every new signal is explained in the evidence list in plain words.
- A failed or blocked lookup lowers the score a little, never breaks the page.

## Non-goals

- No tenant identification. An IP range says "Microsoft 365", not whose. The tenant question is in the partner tenant spec.
- No ASN lookups in version 1 (see open questions).
- No paid data sources.
- No changes to how decisions, alerts or reports work.

## Anti-goals

- Overclaiming. Shared infrastructure means a range match says "this is the provider", not "this is authorised by this client". Wording stays "evidence", never "confirmed".
- A flaky page. Lookups run in the background and are cached. The Senders table never waits on a network call.
- Hammering a provider. Published files are fetched on a schedule, only when they have changed, and cached.

## Design

### New signals and weights

Each signal type counts once per service per source, combined as before (`1 - product(1 - weight)`, capped at 0.99). The new weights, in one place beside the existing ones:

| Signal | Weight | Meaning |
|---|---|---|
| The domain's own SPF record authorises this IP for the service | 0.85 | The IP is inside a range reached through an include the catalogue ties to the service |
| Forward-confirmed reverse DNS under a service's domain | 0.60 | The IP's reverse name belongs to the service, and that name resolves back to the IP |
| The IP is inside the provider's published ranges | 0.70 | From Microsoft's web service, Google's record chain or AWS's file |

The published-range signal replaces the RDAP owner-name signal when both match, because the same fact from a better source should not count twice (the larger of the two is used). The SPF range signal replaces the name-only SPF include signal in the same way.

An unconfirmed reverse name is ignored, since the owner of an IP block can set any reverse name they like.

### SPF ranges

When the SPF check runs for a domain (it already runs daily), also expand the record: follow `include:` and `redirect=` chains using the existing TXT lookup, collect `ip4:` and `ip6:` mechanisms, and tag each range with the include it came from. The `a` and `mx` mechanisms are collected too, since they are the domain's own servers.

Includes are mapped to catalogue services by a new `SpfIncludeHosts` list on each catalogue entry (the existing include names in `SpfIncludeCatalog` seed it). Ranges from an include with no catalogue service are kept but unnamed (see open questions).

Limits so a hostile or huge record cannot hurt us: a depth cap, a cap of 50 lookups per domain (higher than the 10 SPF allows, because we are reading, not evaluating), a visited-set against loops, and a timeout per lookup. If expansion fails, the last good result is kept.

Stored on the domain as a compact JSON list of `{ service, cidr }`, replaced on each successful expansion.

### Reverse DNS

For each source IP in the 30-day window that has no strong match yet, look up its reverse name, then look up that name's address and require it to include the IP (forward-confirmed reverse DNS). Match the name's domain against a new `ReverseDnsSuffixes` list on each catalogue entry (for example Microsoft's `protection.outlook.com`, Google's `1e100.net` and `google.com`). Results, including "no reverse name", are cached per IP in the existing IP lookup cache with a retry window like RDAP's.

### Provider ranges

A background refresher, the same shape as the existing IP enrichment and MTA-STS cycles:

- **Microsoft:** read the version endpoint at most once an hour, fetch the Exchange Online ranges only when the version has moved, and store the CIDRs.
- **Google:** expand `_spf.google.com` and the records it references, store the CIDRs, refresh daily.
- **AWS:** fetch `ip-ranges.json` weekly, keep only the service codes the builder confirms are for email.

Ranges are stored in a new `ProviderIpRanges` table (`Provider`, `Cidr`, `FetchedUtc`) so a restart does not refetch. A new setting turns the outbound fetches off for installs that cannot reach the internet, with the existing signals still working. The Microsoft call needs a client request ID, which is a random GUID made once per install and not derived from anything about the user.

### Where it shows

Nothing new on screen. The evidence list gains lines such as "Your SPF record authorises this IP for Mailchimp", "Its reverse name, mail-eopbgr.protection.outlook.com, belongs to Microsoft 365" and "This IP is in Microsoft's published Exchange Online ranges".

### Failures

Every failure is quiet on the page and visible in the server log with the provider's request ID where there is one. In settings, a small status line per provider says when its ranges were last refreshed and, if the last attempt failed, why and what to do (for example "Microsoft returned 429. dotMARC will retry in an hour.").

## Constraints

- All lookups run off the render path and are cached.
- No identifying data leaves the install. The only requests are the domain's own public DNS records, reverse and forward DNS for public IPs already in reports, and public provider files.
- The new signals reuse the existing scoring function, so the Senders table, client report, portal and API all improve together.
- No em dashes in any copy.

## Testing

- CIDR containment for IPv4 and IPv6, including edges and malformed input.
- SPF expansion with a fake TXT lookup: nested includes, loops, `redirect=`, the lookup cap, a failing lookup keeping the last good result, and tagging ranges by include.
- Forward-confirmed reverse DNS with a fake resolver: confirmed, a reverse name that does not resolve back, and no reverse name.
- Provider file parsers against recorded sample files, including a changed version and an empty response.
- Scoring: each new signal alone and combined, a range match replacing the owner-name signal instead of adding to it, and the cap.
- The refresher honours the Microsoft version check and the off setting.
- An evaluation corpus: a set of anonymised report records with the service a person says is right, run as a test that reports how many are named correctly. This is how we know whether a weight is off.

## Phasing

1. **Phase 1:** SPF range expansion and its signal. No new outbound traffic beyond the DNS we already make.
2. **Phase 2:** forward-confirmed reverse DNS and its signal.
3. **Phase 3:** provider ranges (Microsoft, Google, AWS), the refresher, the table and the off setting.
4. **Phase 4:** the catalogue as data (see open questions), and unnamed includes shown as senders.

## Open questions

1. **Evaluation data.** The accuracy test needs real examples. Would you share a few anonymised reports from your own clients, with the services you know are right, so the weights can be checked against something real? Without them, the weights stay guesses.
2. **Unnamed includes.** If a domain's SPF includes a service that is not in the catalogue, I propose showing it as a sender named after the include's host (for example "mail.vendor.example") with a note that it is not in the catalogue. It helps us notice services to add. Is that wanted?
3. **Catalogue as data.** Moving the catalogue into a JSON file that ships with the app, with an optional override file on disk, would let it grow without code changes and let an install add its own services. Worth doing in Phase 4?
4. **ASN lookups.** The network operator (ASN) is more reliable than the RDAP name. Looking it up needs another source, such as a DNS-based mapping service, and I have not checked the terms for one. I left it out of version 1 and would check it as a follow-up.
5. **Default for outbound fetches.** I propose on by default, with the setting to turn it off.

## My calls

- Weights of 0.85, 0.60 and 0.70, with the replacement rules above.
- A lookup cap of 50 per domain for SPF expansion.
- Unconfirmed reverse names are ignored.
- Provider files are refreshed daily (Microsoft hourly at most, only when the version moves, AWS weekly).
