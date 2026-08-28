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
comes from the response's `entities` (the registrant/administrative vCard), country from the
top-level `country` field when present, falling back to an entity's address country if not.
RDAP structure varies slightly between RIRs in practice, so parsing is defensive: missing fields
produce a partially-populated `IpInfo` (e.g. organization known, country unknown) rather than a
failure.

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
