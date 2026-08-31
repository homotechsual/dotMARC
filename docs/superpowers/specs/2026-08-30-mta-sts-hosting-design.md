# MTA-STS Hosting Design

## Overview

dotMARC monitors DMARC for customer domains but has no story for MTA-STS (RFC 8461), a
complementary mechanism that lets a domain publish a policy over HTTPS at
`https://mta-sts.<domain>/.well-known/mta-sts.txt` telling receiving mail servers which MX hosts
are legitimate for that domain. This design adds *hosting* that policy — not just telling a
customer to host it themselves, but dotMARC serving it on their behalf, the same way it already
polls their DMARC reports on their behalf.

Hosting a policy for a domain means serving `mta-sts.<domain>` over valid TLS — an
admin-controlled, growing set of hostnames dotMARC doesn't know in advance, decided entirely by
which customers opt in. Today's deployment (Docker Compose, Azure Container Apps) has no mechanism
for that at all: it's a single hostname, single certificate, decided at deploy time.

This design covers the two deployment targets dotMARC already supports. Multi-tenant SaaS is an
explicit non-goal — see below.

## Goals

- For each monitored `Domain` a customer opts into, serve a correctly-formatted MTA-STS policy at
  `https://mta-sts.<domain>/.well-known/mta-sts.txt`, with the customer only having to add one
  CNAME record.
- Work identically for self-hosted Docker Compose and Azure Container Apps deployments, reusing the
  same data model, DNS verification, and serving logic across both — only the TLS/certificate
  provisioning mechanism differs.
- Make onboarding progress visible: a customer who just added a CNAME should be able to see
  "waiting for DNS" vs. "provisioning" vs. "live" vs. "something's wrong," not a single opaque
  pending state.
- Default new configurations to `testing` mode with a short `max_age` — a wrong MX list under
  `enforce` causes real mail rejection, so nothing should start there.

## Non-goals

- Multi-tenant SaaS. There's no tenant boundary in dotMARC's data model today (one deployment = one
  organization's data, even in the MSP case) — MTA-STS hosting at real multi-tenant scale needs an
  edge layer built for many customer-owned domains (Azure Front Door's SaaS custom-domain support,
  or Cloudflare for SaaS) and inherits whatever tenant boundary that later work introduces. Not
  this design.
- Auto-suggesting MX hosts from the domain's actual MX records. `MtaStsMxHosts` is manually entered
  for now; looking up the real MX records to pre-fill or validate against is a natural follow-up.
- A generic multi-domain TLS platform for anything other than `mta-sts.*` traffic. The self-hosted
  Caddy instance this design adds is scoped to that traffic only — it does not take over the main
  dotMARC hostname's own TLS, which stays exactly as documented today (bring your own reverse
  proxy).
- SPF/DKIM checking, or anything about the DMARC record itself — that's `DmarcDnsChecker`'s job,
  unrelated to this design.

## Data model

`Domain` gains:

- **`MtaStsStatus`** (enum: `NotConfigured`, `PendingDns`, `PendingCertificate`, `Active`,
  `Failed`). Default `NotConfigured`. Unlike `DmarcCheckStatus`, a `Failed` state exists here and is
  surfaced distinctly rather than silently retried with the prior status left unchanged — MTA-STS
  hosting is a multi-step provisioning flow a customer is actively waiting on, not a passive
  read-only check, so silence would read as "nothing is happening" rather than "hosting is
  broken."
- **`MtaStsCheckedUtc`** (`DateTimeOffset?`) and **`MtaStsCheckDetail`** (`string?`) — same shape as
  the DMARC fields.
- **`MtaStsEnabled`** (`bool`, default `false`) — the opt-in toggle, independent of status.
  Disabling it drives teardown (see Provisioning).
- **`MtaStsMode`** (enum: `Testing`, `Enforce`, `None` — default `Testing`) — the policy's own
  `mode` field per RFC 8461 §3.
- **`MtaStsMaxAgeSeconds`** (`int`, default `604800` / one week — not the spec's one-year maximum).
  A short default keeps early mistakes cheap to fix before committing receivers to a long cache
  lifetime.
- **`MtaStsMxHosts`** (`List<string>`) — needs the same value-converter + explicit `ValueComparer`
  pattern as `Role.Permissions` in `DotMarcDbContext.cs` (EF Core throws at runtime without the
  explicit comparer for a converted list property — not merely a warning).

One migration adds all of the above: `AddColumn<string>` with an explicit `defaultValue` for each
enum column (matching the existing `DmarcCheckStatus` migration's style), `AddColumn<bool>` for
`MtaStsEnabled`, `AddColumn<int>` for `MtaStsMaxAgeSeconds`, and the array-backed column plus
`ValueComparer` for `MtaStsMxHosts`.

## DNS verification

A new `IMtaStsDnsVerifier`, structured exactly like `IDmarcDnsChecker` (typed `HttpClient`,
Cloudflare's DNS-over-HTTPS JSON API), but querying `mta-sts.<domain>` as type CNAME (5) instead of
`_dmarc.<domain>` as TXT (16), checking that the answer resolves — directly or through the chain —
to dotMARC's own configured hosting hostname (a new `MtaSts:HostingHostname` config value, set
per-deployment):

```csharp
public interface IMtaStsDnsVerifier
{
    Task<MtaStsDnsVerificationResult> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken);
}
```

Result is one of `Resolved`, `NotFound`, `PointsElsewhere` — the last one is worth distinguishing
in `MtaStsCheckDetail` from "not set up yet," since it usually means a copy-paste mistake in the
CNAME's target rather than the record simply not existing yet.

## Onboarding state machine

```
NotConfigured --(customer enables + configures mode/hosts/max_age)--> PendingDns
PendingDns --(CNAME verified)--> PendingCertificate
PendingCertificate --(serving self-check succeeds)--> Active
PendingCertificate/Active --(provisioning error or broken self-check)--> Failed
Failed --(retried on the fast cadence; self-heals once fixed)--> PendingCertificate/Active
```

The `PendingCertificate` → `Active` transition is the same self-check on **both** deployment
targets — the key simplification this design relies on. After DNS is verified (and, on Azure, after
the custom domain is bound — see Provisioning), the background cycle issues a real `GET
https://mta-sts.<domain>/.well-known/mta-sts.txt` and checks for a 200 with the expected body:

- On Caddy, this request is itself what triggers on-demand certificate issuance the first time it
  succeeds through the proxy.
- On Azure, it's a pure health check confirming the ARM-provisioned binding has gone live.

No separate webhook or ARM-status-polling mechanism is needed for Azure — the self-check Caddy
needs anyway doubles as the Azure "did provisioning finish" signal, avoiding a second code path (and
avoiding Azure Event Grid/webhook plumbing entirely).

## Serving the policy

Two new unauthenticated minimal-API endpoints in `Program.cs`, registered the same way as the
existing `/demo/sign-in/{persona}` endpoint (after `UseAuthentication`/`UseAuthorization`/`UseAntiforgery`,
before `MapRazorComponents`, `.AllowAnonymous()`):

- **`GET /.well-known/mta-sts-ask?domain=mta-sts.<domain>`** — Caddy's on-demand-TLS "ask"
  callback. Strips the `mta-sts.` prefix, looks up the `Domain`, returns 200 only if
  `MtaStsEnabled` and `MtaStsStatus` is `PendingCertificate`, `Active`, or `Failed` (i.e. DNS has
  already been verified — Caddy should not attempt issuance before that), else 404.
- **`GET /.well-known/mta-sts.txt`** (matched by the `Host` header on `mta-sts.*`) — strips the
  prefix, looks up the `Domain`, and if `MtaStsEnabled`, renders the policy as `text/plain` (RFC
  8461's required content type) from `MtaStsMode`/`MtaStsMxHosts`/`MtaStsMaxAgeSeconds`. 404 if the
  hostname isn't a configured domain. Rendering the policy text itself is a small pure function,
  independent of everything else — trivially unit-testable.

## Provisioning: Caddy vs. Azure

```csharp
public interface IMtaStsHostProvisioner
{
    Task EnsureProvisionedAsync(string domainName, CancellationToken cancellationToken);
    Task TeardownAsync(string domainName, CancellationToken cancellationToken);
}
```

Selected via DI based on deployment config, the same way the codebase already branches on
`demoOptions.Enabled` in `Program.cs`.

- **`CaddyMtaStsHostProvisioner`** (self-hosted): both methods are no-ops. Caddy's on-demand TLS
  handles issuance implicitly the first time a request succeeds through the "ask" endpoint; there's
  nothing for the app to push. Teardown is likewise implicit — once `MtaStsEnabled` is false, "ask"
  starts 404ing and Caddy stops renewing.
- **`AzureMtaStsHostProvisioner`**: `EnsureProvisionedAsync` calls the Azure Resource Manager API
  (`Azure.ResourceManager.AppContainers`) to add `mta-sts.<domain>` to the Container App's
  `customDomains` and request a managed certificate — the step that needs the new RBAC grant (see
  Deployment changes). `TeardownAsync` removes the binding when a customer disables hosting, so
  bindings don't accumulate; the managed certificate resource itself is left in place rather than
  deleted, since re-creating it on a later re-enable would mean waiting on domain validation again
  for no benefit. Provisioning takes minutes, not instant; the shared self-check above is what
  confirms completion.

`TeardownAsync` is called from a small pass at the start of `RunMtaStsCheckCycleAsync`, separate
from the main staleness-driven loop: domains where `MtaStsEnabled` is now false but `MtaStsStatus`
hasn't been reset to `NotConfigured` yet (i.e. disabled since the last cycle) — the main loop's
query only looks at *enabled* domains, so without this separate pass a disabled domain would never
be revisited and an Azure binding would stay orphaned forever.

## Trigger and cadence

A new sub-cycle in `PollingService` (`RunMtaStsCheckCycleAsync`), structured exactly like the
existing DMARC cycle: its own Postgres advisory-lock key (the next unused `84_200_*` constant —
grep for all values in use before picking one, don't assume the next integer is free), its own
try/catch in `ExecuteAsync` so a failure here doesn't affect the mailbox poll or the DMARC cycle.

Two staleness windows within the one cycle, since onboarding benefits from much faster feedback
than an already-stable domain needs:

- `PendingDns` / `PendingCertificate` / `Failed`: re-check every ~15 minutes.
- `Active`: re-verify every 24 hours (same cadence as the DMARC cycle) — catches certificate
  renewal failures or an accidentally-removed CNAME.

## Failure handling

Deliberately different from `DmarcDnsChecker`'s "leave status unchanged on failure" policy, because
here a customer is actively watching onboarding progress rather than passively benefiting from a
background check:

- DNS not yet resolving is not a failure — stays `PendingDns` with `MtaStsCheckDetail` updated
  ("waiting for the CNAME to resolve to `<hosting hostname>`"), never `Failed`.
- A definitive provisioning error (Azure ARM returns an actual error, not just "still working")
  moves to `Failed` with the error detail.
- A serving self-check that stops succeeding on a previously-`Active` domain moves to `Failed` —
  this is a regression, not an onboarding wait state, and should read differently in the UI.
- `Failed` is not a dead end: the same cycle keeps retrying it on the fast (~15 minute) cadence, so
  a transient problem self-heals without manual intervention.

## Permissions

Two new `Permission` enum members: `MtaStsView`, `MtaStsManage` — mirrors the existing
`DomainsView`/`DomainsAdd` split. Both auto-register as authorization policies via the existing
`foreach` in `Program.cs`'s `AddAuthorization` call; no other registration code needed. Both get
added to `website/docs/permissions-and-access.mdx`'s Roles section.

## UI

A new tab on the domain detail page, alongside the existing Overview/Sources/Raw Reports tabs —
not a new top-level page, and not added to Manage Domains. Manage Domains is configuration-only by
established precedent (see the DMARC DNS status design's non-goals); MTA-STS needs configuration
*and* live status/detail together in one place, the same reason DMARC status lives on the Overview
tab rather than on Manage Domains. Gated with `<AuthorizeView Policy="MtaStsView">` /
`"MtaStsManage"`, matching `ManageDomains.razor`'s existing pattern.

## Deployment changes

**Docker Compose**: a new `caddy` service (`caddy:2-alpine`), a mounted `Caddyfile` with an
`on_demand_tls` block whose `ask` points at `http://app:8080/.well-known/mta-sts-ask`, a `mta-sts.*`
host matcher reverse-proxying to `app:8080`, and a volume for certificate storage persistence. It
publishes 443 (and 80, for the HTTP-01 challenge) — but is scoped to `mta-sts.*` traffic only; it
does not take over the main dotMARC hostname's TLS. Docs need to cover the case where something is
already bound to port 443 on the same host: if it's already Caddy, merge the provided
`on_demand_tls` + `mta-sts.*` block into that existing instance instead of starting a second one
(two processes can't both bind 443); if it's something else, route `mta-sts.*` traffic to the
bundled Caddy on an internal port instead, or vice versa.

**Azure (`infra/main.bicep`, gated behind `enableMtaStsHosting`, off by default)**: no built-in RBAC
role exists at the granularity of "manage custom domains" specifically — Azure's action set for
this resource type doesn't separate that from "manage everything about this container app"
(`Microsoft.App/containerApps/*/write` is the only write action, and it also covers image/scale/env
too). Rather than one combined custom role, this is two separate custom
`Microsoft.Authorization/roleDefinitions`, each assigned only at the one resource it actually
needs — narrower than a single resource-group-wide assignment would be, and narrower than the
built-in Container Apps Contributor role either way (which additionally bundles alert-rule
management and environment create/join rights this identity has no use for):

- `containerApps/read` + `containerApps/write`, assigned scoped to the Container App itself.
- `managedEnvironments/read` + `managedEnvironments/managedCertificates/{read,write}`, assigned
  scoped to the Container Apps environment itself.

No new compute resource — the ARM calls happen from the existing app process. The Container App's
own generated hostname isn't known until after a first deployment, so `mtaStsHostingHostname`
starts blank and is set on a follow-up deployment once that hostname is known (the same two-step
pattern the OIDC redirect URI setup already uses).

## Testing

Following the DMARC DNS status design's precedent exactly: `Testcontainers.PostgreSql` for
anything touching the database, a fake `HttpMessageHandler` (matching `FakeHttpMessageHandler`) for
anything touching DNS/HTTP, and an explicit acknowledgment of what isn't automated:

- `IMtaStsDnsVerifier`: each result (`Resolved`/`NotFound`/`PointsElsewhere`) against a fake
  Cloudflare response, same style as `DmarcDnsChecker`'s waterfall tests.
- Policy text rendering: a pure function, unit-tested directly against
  `MtaStsMode`/`MtaStsMxHosts`/`MtaStsMaxAgeSeconds` combinations — no DB or network needed.
- State machine transitions: `PollingService` integration tests via `Testcontainers.PostgreSql` —
  `PendingDns` → `PendingCertificate` on a successful verify; stays put with detail updated on
  failure; `Active` → `Failed` on a broken self-check; the two staleness windows (15 minutes vs. 24
  hours) each pick up the right domains.
- `IMtaStsHostProvisioner`: real Caddy ACME behavior and real Azure ARM provisioning are not
  testable in CI. Kept behind this thin interface with a fake implementation for every test that
  needs one — mirroring this project's existing acceptance that some surface area (there, Blazor
  component rendering) has no automated coverage and is verified manually instead.
- The two new minimal-API endpoints: worth a lightweight integration test if the codebase already
  has a `WebApplicationFactory`-style harness anywhere; otherwise that's a gap to flag rather than
  new test infrastructure to build as a side effect of this feature.
