# Source IP enrichment (RDAP ownership + country)

## Goal

Show who actually owns a source IP directly on the Sources tab, so investigating a DMARC
failure (or an unfamiliar sender) doesn't require pasting the IP into an external WHOIS tool by
hand. Enriches each source IP with its registered organization and country.

## Non-goals

* City-level or coordinate geolocation. Registry country from RDAP is enough for "is this
  plausibly who/where I'd expect" — a second geolocation dependency adds another thing that can
  rate-limit or go stale for no real benefit here.
* Background/poll-cycle enrichment. Lookups happen on demand, the first time a given IP is
  actually viewed, not proactively for every IP ingestion ever sees.
* Any UI surface beyond `DomainDetail.razor`'s Sources tab.

## Data model

A new entity, `IpInfo`, keyed by the IP address itself (source IPs are shared across domains and
reports, so caching by IP — not by `ReportRecord` — means one lookup serves every future view of
that IP anywhere in the app):

```csharp
public enum IpLookupStatus { Ok, NotFound, LookupFailed }

public sealed class IpInfo
{
    public required string Ip { get; set; }          // primary key
    public string? Organization { get; set; }
    public string? Country { get; set; }
    public IpLookupStatus Status { get; set; }
    public DateTimeOffset LookedUpUtc { get; set; }
}
```

* `Status = Ok`: cached indefinitely. IP block ownership changes rarely enough that re-querying
  on a schedule isn't worth it (unlike the DMARC DNS check, which re-checks every 24h because DNS
  records genuinely do change often).
* `Status = NotFound` or `LookupFailed`: retried after 24h, in case the miss was transient
  (RDAP server hiccup, a not-yet-delegated block that gets registered later) — mirrors this
  codebase's existing "leave it, retry later" convention (see `PollingService`'s DMARC check
  cycle doc comment).

## Lookup service

`IIpInfoLookup` / `RdapIpInfoLookup`, following the exact shape of the existing
`IDmarcDnsChecker`/`DmarcDnsChecker` (`src/DotMarc/Dns/`): a thin `HttpClient`-based adapter,
registered the same way (`AddHttpClient<IIpInfoLookup, RdapIpInfoLookup>`).

A single GET to `https://rdap.org/ip/{ip}` — the public RDAP bootstrap redirector — which
redirects to whichever RIR (RIPE, ARIN, APNIC, LACNIC, AFRINIC) actually holds that address
block. `HttpClient` follows redirects by default, so this needs no bootstrap/dispatch logic of
its own: one URL, global coverage. The response is IETF RDAP JSON (RFC 9083); organization name
comes from the response's `entities` (the registrant/administrative vCard), country from
the response's top-level `country` field, when present. Several RIRs — notably ARIN — do not
populate this field at all, and RDAP entities' address information (where present) is free-text
rather than structured, so there is no safe fallback to parse a country from it; a domain whose
sources are mostly ARIN-registered (which in practice means most large US-based mail senders —
Google, Microsoft, Yahoo, Amazon) will show a blank Country for many rows. This is an accepted
limitation of the data source, not a bug. RDAP structure varies slightly between RIRs in
practice otherwise too, so parsing is defensive: missing fields produce a partially-populated
`IpInfo` (e.g. organization known, country unknown) rather than a failure.

## UI and interaction

Two new columns on the Sources tab's table (`DomainDetail.razor`): **Owner** and **Country**,
alongside the existing Source IP / Volume / SPF / DKIM / Disposition.

`DomainDetail.razor` currently loads all three tabs' data eagerly in `OnInitializedAsync` — Sources,
Reports, and the chart are computed upfront regardless of which tab is visible. Adding blocking
RDAP calls there would slow down every domain page load, including for people who never open the
Sources tab. Instead:

1. `OnInitializedAsync` renders the Sources table immediately using only already-cached `IpInfo`
   rows (a single indexed DB query, no network calls).
2. Any source IP with no cached `IpInfo` (or a stale `NotFound`/`LookupFailed` one) triggers a
   background lookup — a small number in parallel, not all at once — that writes the result to
   the database and then updates that row's display via `StateHasChanged()`.
3. Until its lookup resolves, an uncached row shows a small loading indicator in place of
   Owner/Country rather than blocking the rest of the page.

This relies on Blazor Server's persistent per-circuit connection, which already backs every
other real-time update in this app. Each background lookup uses its own short-lived
`DotMarcDbContext` from `IDbContextFactory` to write the result — not the context `OnInitializedAsync`
already disposed of by the time a background lookup completes — matching this codebase's existing
convention (see `DomainDetail.razor`'s own doc comment on why it uses the factory rather than an
injected, circuit-lifetime context).

## Testing

* `RdapIpInfoLookup` parsing logic is exercised the same way `DmarcDnsChecker` is tested today:
  fixture RDAP JSON responses (one representative of each RIR's typical shape) fed through a
  fake `HttpMessageHandler`, asserting the right organization/country come out, including the
  defensive-parsing paths (missing `country`, empty `entities`).
* A Postgres-backed test confirms the cache behavior: a `Status = Ok` row is never re-queried, a
  `NotFound`/`LookupFailed` row older than 24h is retried, one younger than 24h is not.
* No test can meaningfully assert against the live `rdap.org` service in CI; the fake-handler
  tests above are the coverage, matching how `DmarcDnsChecker` itself is tested.

## Configuration

No new configuration. `https://rdap.org` needs no API key and has no documented rate limit
significant at this app's scale (looking up unique source IPs on demand, cached indefinitely
after the first success).

## Addendum: IPv6 URL-escaping fix and range-based batch caching (2026-08-30)

`RdapIpInfoLookup` built its request path with `Uri.EscapeDataString(ip)`, which percent-encodes
`:` to `%3A`. rdap.org's redirector 400s on that, so every IPv6 lookup failed — 100%
reproducibly, not a transient issue. IPv4 was unaffected only because dotted-decimal has no
characters that need escaping. Fixed by validating with `IPAddress.TryParse` and interpolating
the canonical `IPAddress.ToString()` unescaped into the path (legal per RFC 3986); an unparsable
`SourceIp` now short-circuits to `LookupFailed` without a network call.

Separately, many source IPs seen in practice share the same RDAP allocation block (e.g. several
of one sender's outbound relays), so a new `IpRange` table caches the whole block an `Ok` lookup
resolves to — keyed by the RDAP response's `startAddress`/`endAddress` (mandatory fields per RFC
9083, extracted by `RdapResponseParser.ParseRange`), applied to both IPv4 and IPv6:

* `IpInfoService.EnrichAsync` upserts the range alongside its existing per-IP `IpInfo` upsert,
  whenever the lookup result carries bounds. No `Status`/retry semantics: a range is only ever
  written from an `Ok` result (a failed lookup has no reliable bounds to cache), and `Ok` is
  already cached indefinitely.
* `IpRangeMatcher.FindContaining` is a pure, in-memory containment check against all cached
  ranges (loaded in full — the number of distinct allocation blocks ever seen is expected to
  stay small relative to the individual IPs they cover).
* `DomainDetail.razor`'s `OnInitializedAsync` checks the range cache before falling back to a
  background `EnrichAsync` call: an IP covered by an already-cached range renders immediately,
  with no HTTP call, no throttle permit spent, and no new `IpInfo` row written for that literal
  IP — the existing per-IP cache and this addition compose without either needing to change the
  other's shape.
