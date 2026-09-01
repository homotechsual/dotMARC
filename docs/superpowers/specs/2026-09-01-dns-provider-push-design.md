# DNS Provider Push Design

## Overview

dotMARC tells a customer which DNS record to add — a CNAME for MTA-STS hosting, a TXT record for
DMARC — and then waits for them to go do it by hand at their registrar or DNS host. This design
adds a "push it for me" option for the two providers common among dotMARC's MSP audience:
Cloudflare and Azure DNS. It does not add a credential store: every push is its own fresh,
interactive OAuth consent round-trip, and the resulting token is used once and discarded.

## Goals

- Detect which of Cloudflare or Azure DNS hosts a given domain, from its public NS records.
- Let a user push a record change through that provider's own API, authenticated as themselves via
  that provider's own OAuth consent screen — no long-lived credential ever stored by dotMARC.
- Cover the two record-changing flows that already exist: MTA-STS's `mta-sts.<domain>` CNAME (from
  Manage MTA-STS), and the DMARC `_dmarc.<domain>` TXT record (from the domain detail page's
  Overview tab).
- For an existing, misconfigured `_dmarc` TXT record, never overwrite it silently — show a
  before/after diff and require explicit confirmation, since it may carry tags (`sp=`, `pct=`,
  `adkim=`, etc.) a customer set on purpose.
- Where detection fails, or the provider isn't one of these two, or push isn't configured: today's
  manual instructions, unchanged. No regression for anyone.

## Non-goals

- Persisting any provider credential, token, or refresh token anywhere — not encrypted, not
  short-lived-cached. Every push re-authenticates.
- The cross-domain DMARC authorization record (`<domain>._report._dmarc.<mailbox-domain>`). It
  lives in the *mailbox's* domain, not the customer's — for a typical install that's the MSP's own
  domain, set up once, not a per-client action. Stays documented-manual.
- Providers other than Cloudflare and Azure DNS. The detector recognizes only these two; everything
  else falls back to manual instructions, same as an install that hasn't configured either OAuth
  app.
- Auto-fixing anything about `_dmarc` beyond the `rua=` tag. A record that doesn't parse as
  `v=DMARC1; ...` at all gets a full-content warning and an explicit "replace entirely" choice, not
  a merge.

## Provider detection

A new `IDnsProviderDetector`, structured like `IMtaStsDnsVerifier`/`DmarcDnsChecker`: queries the
domain's NS records via Cloudflare's DNS-over-HTTPS JSON API (type 2) and pattern-matches the
returned hostnames:

```csharp
public interface IDnsProviderDetector
{
    Task<DetectedDnsProvider> DetectAsync(string domainName, CancellationToken cancellationToken);
}

public enum DetectedDnsProvider { Unknown, Cloudflare, AzureDns }
```

- Any NS hostname ending `.ns.cloudflare.com` → `Cloudflare`.
- Any NS hostname ending `.azure-dns.com`, `.azure-dns.net`, `.azure-dns.org`, or `.azure-dns.info`
  → `AzureDns` (the four standard Azure Public DNS name server suffixes).
- No match, or the NS lookup itself fails → `Unknown`.

A domain with NS records split across providers (mid-migration) matches whichever provider the
*first* matching NS hostname belongs to — an edge case rare enough not to warrant more.

## The record-change model

A single value type both flows produce, independent of provider:

```csharp
public sealed record DnsRecordChange(
    DnsRecordChangeKind Kind,          // Create or Merge
    string RecordType,                 // "CNAME" or "TXT"
    string Name,                       // e.g. "mta-sts.contoso.com" or "_dmarc.contoso.com"
    string DesiredValue,
    string? ExistingValue);            // set only for Kind == Merge

public enum DnsRecordChangeKind { Create, Merge }
```

- Manage MTA-STS produces a `Create` (CNAME never exists yet if the button is showing — MTA-STS
  status is `PendingDns` precisely because it doesn't).
- The Overview tab produces `Create` when `DmarcCheckStatus` is `MissingOwnRecord`, or `Merge` (with
  `ExistingValue` set to the record `DmarcDnsChecker` already fetched) when it's `Misconfigured` due
  to the `rua=` tag.

For a `Merge`, a small pure function parses the existing value's `;`-delimited tags, replaces (or
appends) `rua=mailto:<mailbox>`, and rejoins — preserving every other tag untouched. If the existing
value doesn't start with `v=DMARC1`, merge isn't attempted; the UI shows it as a full replacement
with an extra warning instead.

## Auth model: ephemeral OAuth, nothing persisted

Each dotMARC instance registers itself as an OAuth client with each provider it wants to support —
a one-time step by whoever runs the instance, alongside the existing mailbox and dashboard app
registrations in `getting-started.mdx`. New optional config, both independently optional (unset
means that provider's push button never renders — pure fallback to manual instructions):

- `CloudflareDns__ClientId`, `CloudflareDns__ClientSecret` — a Cloudflare self-managed OAuth client
  (Authorization Code flow, server-side/confidential client type), scoped to the `Zone.DNS` edit
  permission group. Registered via `POST /accounts/{account_id}/oauth_clients` per Cloudflare's
  OAuth client docs.
- `AzureDns__TenantId`, `AzureDns__ClientId`, `AzureDns__ClientSecret` — a *third*, separate Entra
  app registration (distinct from the existing mailbox and dashboard apps, per the established "do
  not reuse app registrations" precedent), requesting delegated `https://management.azure.com/user_impersonation`.
  The push only succeeds if the signed-in user's own Azure RBAC actually grants them
  `Microsoft.Network/dnsZones/*/write` on the target zone — dotMARC inherits the user's own access,
  never holds a standing grant of its own.

> **Implementation-time note:** Cloudflare's published docs describe OAuth client *registration*
> in full, but not the runtime authorize/token endpoint URLs for this specific third-party-consent
> flow — confirm those against Cloudflare's OAuth docs (or its discovery metadata, if published)
> when wiring up `CloudflareDnsPushProvider`, rather than assuming the pattern used by Cloudflare
> Access's unrelated per-team-name SSO endpoints.

Two new minimal-API endpoints per provider, registered the same way as the existing
`/.well-known/mta-sts.txt` endpoints in `Program.cs`:

- **`GET /dns-push/{provider}/start`** — takes the pending `DnsRecordChange` (identified by domain
  ID + which field it's for, e.g. `?domainId=42&target=mta-sts`), encodes it into a signed,
  short-lived `state` parameter (ASP.NET Core's `IDataProtector`, a few minutes' expiry — no
  server-side session needed between redirect-out and callback-in), and redirects to the provider's
  consent screen.
- **`GET /dns-push/{provider}/callback`** — validates and unprotects `state`, exchanges the
  authorization `code` for an access token, resolves the matching zone (`GET /zones?name=` for
  Cloudflare; listing DNS zones in-subscription for Azure), pushes the one record, and redirects
  back to the originating page with a result flag for a success/failure toast. The token is held in
  a local variable for the duration of this one request and never touches the database, a cache, or
  disk.

## UI

- **Manage MTA-STS**: next to a domain whose status is `PendingDns`, a "Push via Cloudflare" (or
  "…via Azure DNS") button appears only when detection matched *and* that provider's OAuth app is
  configured. Absent either condition, today's CNAME instructions are all that's shown — unchanged.
- **Domain detail (Overview tab)**: same idea next to `DmarcCheckDetail` when status is
  `MissingOwnRecord` (one-click) or `Misconfigured` due to `rua=` (opens a before/after diff dialog,
  matching the existing `ConfirmDeleteDomainDialog`-style pattern, with an explicit "Apply" action —
  never auto-applied).

## Error handling

- **Consent denied / user cancels**: back on the originating page, neutral "not pushed" message —
  manual instructions still shown as normal, no error noise.
- **Token doesn't cover the target zone** (wrong Cloudflare account authorized, or the Azure user
  lacks RBAC on that zone): the zone-lookup call simply returns nothing; surfaced as "couldn't find
  `<domain>` in the account you authorized — check you picked the right one," not a raw API error.
- **Multiple matching zones**: picks the exact name match; if genuinely ambiguous, shows the list
  and asks which one rather than guessing.
- **Provider `Unknown`, or detected but that provider's OAuth app isn't configured**: no button
  renders. Same manual instructions as today.

## Testing

Following this codebase's established split between pure logic and unmockable external I/O:

- `IDnsProviderDetector`'s NS-pattern matching: unit-tested against a fake DNS-over-HTTPS response,
  same style as `DmarcDnsChecker`'s waterfall tests — one case per provider, plus `Unknown` for no
  match and for a lookup failure.
- The `rua=` merge function: a pure function, unit-tested directly against a table of existing
  record values (various tag combinations, a record with no `rua=` at all, a record that doesn't
  parse as DMARC) — no DB or network needed.
- `state` parameter round-tripping (encode, then decode + expiry check): unit-tested directly
  against `IDataProtector`, no HTTP involved.
- The actual OAuth exchange and provider API calls: not testable in CI, same acceptance already
  made for `AzureMtaStsHostProvisioner` and `MxHostsLookup` — verified live once implemented, not
  mocked.
- The two new minimal-API endpoints per provider: a lightweight integration test if a
  `WebApplicationFactory`-style harness already exists in this codebase for the other
  minimal-API endpoints; otherwise a documented gap, not new test infrastructure built as a side
  effect of this feature.

## Docs

- A new `getting-started.mdx` section ("DNS provider push (optional)") covering registering the
  Cloudflare OAuth client and the Azure DNS-scoped Entra app, matching the existing optional-feature
  tone of the MTA-STS-hosting section.
- `mta-sts.mdx` gets a line noting the push button's existence and that it only appears when
  detection succeeds and that provider is configured.
