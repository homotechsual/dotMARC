# Public demo instance with simulated data

## Goal

Run a publicly reachable dotMARC instance at `demo.dotmarc.app`, linked from the marketing
website, that lets a prospective user click around a realistic, fully-functional dashboard
without any real Entra sign-in, real mailbox, or real DMARC data. Visitors can make changes
(add/edit domains, groups, tags, access grants); a nightly job wipes and regenerates the dataset
so the demo never degrades or gets used to attack anything.

This is the same product image as production, running in a new `Demo__Enabled=true` mode that
swaps out real auth/ingestion for fixed personas and generated data — not a fork.

## Non-goals

* Read-only enforcement. The demo is intentionally interactive (see clarifying discussion);
  visitor edits are expected and are wiped by the nightly reset, not prevented.
* High availability / multi-replica / autoscaling. Single container, single small VM, same as the
  rest of this app's supported deployment shapes.
* Perfectly seamless resets. A visitor with an open circuit during the nightly reset may see
  stale or briefly broken state until they navigate again. Documented and accepted (see
  "Reset job" below).
* Any change to the real (non-demo) authentication, ingestion, or authorization code paths. Every
  addition here is gated behind `Demo__Enabled` and is inert — zero behavior change — when unset.

## Architecture summary

```
                      ┌─────────────────────────┐
   demo.dotmarc.app → │ existing shared Caddy    │
                      │ (VM-level, out of repo)  │
                      └───────────┬─────────────┘
                                  │ proxy network, container:8080
                      ┌───────────▼─────────────┐
                      │ dotmarc-demo container   │
                      │  Demo__Enabled=true      │
                      │  - /demo persona picker  │
                      │  - DemoDataResetService  │
                      └───────────┬─────────────┘
                                  │
                      ┌───────────▼─────────────┐
                      │ postgres (demo-only DB)  │
                      └──────────────────────────┘
```

## 1. Demo auth (bypassing Entra)

`Program.cs` currently unconditionally requires `Graph__*`/`EntraId__*` config
(`GraphOptions.ValidateOnStart()`) and wires `AddMicrosoftIdentityWebApp`. A new
`DemoOptions` (`Demo__Enabled`, `Demo__ResetHourUtc`, default `4`) is bound first, and
`Program.cs` branches on `DemoOptions.Enabled`:

* **Demo mode**: skip `GraphOptions` binding/validation, skip registering `PollingService`
  (there is no mailbox to poll — the dataset comes from `DemoDataResetService` instead), and
  register plain ASP.NET Core cookie authentication
  (`AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(...)`)
  instead of `AddMicrosoftIdentityWebApp`.
* **Normal mode**: unchanged — everything below is additive and inert.

New pieces, all under `src/DotMarc/Demo/`:

* `Components/Pages/Demo/DemoSignIn.razor` at route `/demo`, `@attribute [AllowAnonymous]`
  (same pattern as the existing `AccessDenied.razor`/`Error.razor`) — the only page reachable
  without auth in demo mode. Two buttons: "Continue as Demo Admin" and "Continue as Demo Viewer
  (Aurora Retail only)", each a plain HTML form posting to a minimal API endpoint (see below).
* A minimal API endpoint, `POST /demo/sign-in/{persona}` (`persona` is `admin` or `viewer`,
  rejecting anything else with 400), mapped in `Program.cs` only when `Demo__Enabled=true`. It
  builds a `ClaimsPrincipal` with `preferred_username` set to the persona's fixed seeded email
  (`demo-admin@nova-msp.example` / `demo-viewer@nova-msp.example`) and calls
  `HttpContext.SignInAsync`, then redirects to `/`. Nothing else changes:
  `UserAccessClaimsTransformation` already resolves permissions from `UserAccess` by that email,
  so the entire authorization system (roles, scoped groups, every `[Authorize(Policy=...)]`)
  works completely unmodified — this endpoint's only job is producing an authenticated principal
  with the right email claim.
* A small banner in `MainLayout.razor`, rendered only when `Demo__Enabled=true`: "Simulated demo
  data — you're viewing as **Demo Admin**" (or Viewer) with a "Switch persona" link back to
  `/demo`. Switching persona re-hits the sign-in endpoint for the other persona, which overwrites
  the cookie.
* Sign-out (not currently implemented for the real app either) is out of scope; "switch persona"
  covers the only sign-out-like need a demo has.

## 2. The demo data story

A fictional MSP, **Nova MSP**, monitoring DMARC for four clients (`Group` rows) plus one
ungrouped domain:

| Client (Group) | Domains | Story |
|---|---|---|
| Aurora Retail | `aurora-retail.example`, `shop.aurora-retail.example` | Healthy — `p=reject`, ~99.7% pass, clean trend line. This is the Demo Viewer persona's scope. |
| Brightline Legal | `brightline-legal.example` | Ramping up — pass rate climbs from ~70% to ~96% over the last 60 days (a DKIM rollout mid-story), `DmarcCheckStatus=Ok`. |
| Cobalt Freight | `cobalt-freight.example`, `fleet.cobalt-freight.example` | A problem worth investigating — a marketing ESP sending unaligned mail shows up as a distinct failing source IP alongside otherwise-clean traffic (Dashboard "Warning" state). The second domain is `Missing` (no reports in 3+ days). |
| Driftwood Media | `driftwood-media.example` | Legacy, monitor-only — still `p=none`, ~85% pass, `DmarcCheckStatus=MissingAuthorizationRecord`. |
| *(ungrouped)* | `driftwood-events.example` | Shows the "no group" case on the dashboard. |

Reporting orgs are real-world senders (`google.com`, `outlook.com`, `yahoo.com`,
`protonmail.com`), varied per domain. 60 days of history: reports/records generated per domain
per day per active org, with per-domain trend curves (e.g. Brightline's climbing pass rate)
computed as a function of day-index rather than pure noise, so the charts tell the intended
story instead of looking flat/random.

Poll history mirrors the app's own retention convention (see `PollCycle`/
`PollCycleDailySummary` doc comments): the last 7 days as raw `PollCycle` rows (mostly
`Succeeded=true`, one failure for texture), days 8–60 pre-rolled into
`PollCycleDailySummary` rows directly — never generated as raw rows and rolled up, since that's
pure overhead for a seeder. 2–3 `ParseFailure` rows with plausible reasons ("attachment was not
a valid gzip archive") populate that page too.

Two `UserAccess` grants complete the story: `demo-admin@nova-msp.example` → built-in `Admin`
role; `demo-viewer@nova-msp.example` → built-in `Viewer` role, scoped to the Aurora Retail
group.

### Implementation shape

Following this codebase's established "pure core, thin I/O adapter" split (see
`DomainStatistics`, `DmarcReportParser`): a pure `DemoDataGenerator.Generate(Random, DateTimeOffset now)`
returns plain in-memory records (domains, groups, reports, records, poll cycles, parse
failures) with no EF/DB dependency — independently unit-testable (e.g. "Brightline's pass rate
strictly increases over the window", "Cobalt Freight's second domain has no report in the last 3
days"). A thin `DemoDataSeeder` (EF-dependent) takes that output and writes it, plus the two
roles and two `UserAccess` grants, inside a transaction.

## 3. Reset job

`DemoDataResetService : BackgroundService`, registered only when `Demo__Enabled=true`:

* On startup, seeds immediately if the `Domains` table is empty (covers first boot — this
  replaces `AccessBootstrapper`/`InitialAdmins__Emails` in demo mode rather than layering on top
  of it, since the seeder creates the Admin/Viewer roles and the two demo grants itself).
* Otherwise waits until the next `Demo__ResetHourUtc` (default 4) and resets then, repeating
  every 24h via a `PeriodicTimer`.
* A reset deletes all rows from every app-owned table (`Domains`, `Reports`, `ReportRecords`,
  `Groups`, `Tags`, `Roles`, `UserAccesses`, `PollCycles`, `PollCycleDailySummaries`,
  `ParseFailures`, `ProcessedMessages`) in FK-safe order, then runs `DemoDataSeeder` — the exact
  same path used on first boot, so there is only one seeding code path, not two.
* Seeded with `new Random(seed)` where `seed` is derived from the reset run's date, so each
  night's dataset is a fresh variation but a given day's dataset is reproducible if the container
  restarts without crossing a reset boundary.

**Known limitation** (see Non-goals): a visitor mid-session during the 04:00 UTC reset may see
stale or briefly-broken state until they navigate. Accepted as reasonable for a demo.

## 4. Deployment

New `docker-compose.demo.yml`, sibling to the existing `docker-compose.yml` (that file is
untouched):

```yaml
services:
  dotmarc-demo:
    image: ${DOTMARC_IMAGE}
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ConnectionStrings__DotMarc: Host=postgres;Database=dotmarc;Username=dotmarc;Password=${POSTGRES_PASSWORD}
      Demo__Enabled: "true"
    expose:
      - "8080"
    networks:
      - default
      - proxy

  postgres:
    image: postgres:18
    environment:
      POSTGRES_DB: dotmarc
      POSTGRES_USER: dotmarc
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - dotmarc-demo-postgres-data:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U dotmarc -d dotmarc"]
      interval: 5s
      timeout: 5s
      retries: 10

networks:
  proxy:
    external: true

volumes:
  dotmarc-demo-postgres-data:
```

No Caddy container in this repo's compose — the VM's existing shared Caddy instance (already
handling TLS/routing for other Homotechsual sites) routes to `dotmarc-demo:8080` over the shared
`proxy` external network. A reference snippet for that Caddyfile (to be folded in by hand, per
the earlier discussion):

```
{$DOTMARC_DEMO_DOMAIN} {
    reverse_proxy dotmarc-demo:8080
}
```

### CI/CD

New `.github/workflows/demo-deploy.yml`, mirroring the GCT project's own `deploy.yml`
(`.github/workflows/deploy.yml` in the `homotechsual/GCT` repo) pattern exactly (build-and-push
job + SSH deploy job):

* Trigger: `push` to `main`, plus `workflow_dispatch`.
* `build-and-push`: builds and pushes `ghcr.io/homotechsual/dotmarc:demo` (a separate tag from
  the release workflow's semver/`latest` tags, so the demo tracks `main` continuously without
  interfering with tagged-release publishing).
* `deploy`: SSH (same `DEPLOY_SSH_KEY`/`DEPLOY_HOST`/`DEPLOY_USER` secret convention as GCT,
  added fresh for this repo), copies `docker-compose.demo.yml` to the VM, writes a `.env` with
  `DOTMARC_IMAGE`, `POSTGRES_PASSWORD` (new secret, distinct from any other stack's password on
  that VM), and `docker compose -f docker-compose.demo.yml --env-file .env up -d`.
* `DOTMARC_DEMO_DOMAIN` (`demo.dotmarc.app`) is a repository **variable**, not a secret — it's
  not sensitive, and storing it as a secret risks the same output-masking failure mode fixed
  earlier in `release.yml` (a job output containing a secret's value gets silently dropped). It's
  only used for the Caddyfile reference snippet above (folded in by hand), not passed to the
  compose stack's environment.

## Testing

* `DemoDataGenerator` is a pure function — unit tests assert the narrative invariants that matter
  (Brightline's pass rate trends upward, Cobalt Freight's second domain is stale/missing, Aurora
  Retail stays consistently high, counts of domains/groups match the table above) without any
  database.
* An integration-style test (Testcontainers.PostgreSql, matching this repo's existing test setup)
  verifies `DemoDataResetService`'s delete-then-reseed leaves the database in a consistent state
  and that re-running it twice doesn't leave orphaned rows.
* A test confirms `Program.cs` starts successfully with `Demo__Enabled=true` and no
  `Graph__*`/`EntraId__*` configuration present at all (the actual point of this feature).

## Configuration additions

| Variable | Description |
| --- | --- |
| `Demo__Enabled` | Default `false`. When `true`, switches auth to the persona picker and enables `DemoDataResetService`; `Graph__*`/`EntraId__*` become unnecessary. |
| `Demo__ResetHourUtc` | Default `4`. UTC hour the nightly reset runs. |
