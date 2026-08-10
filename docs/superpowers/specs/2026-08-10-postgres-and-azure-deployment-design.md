# PostgreSQL Migration & Azure Deployment Design

## Overview

Replace dotMARC's SQLite database with PostgreSQL, add real EF Core Migrations in place of
`Database.EnsureCreated()`, and ship the infrastructure needed to deploy dotMARC to Azure (Web App
for Containers + Azure Database for PostgreSQL Flexible Server) via a Bicep template — while
keeping self-hosting exactly as simple as it is today via `docker-compose`.

This was prompted by exploring Azure Web Apps as a deployment target: SQLite's file-based storage
doesn't hold up well on Azure App Service's filesystem (not reliably persistent, and SQLite over a
mounted Azure Files share carries real file-locking risk under concurrent access). PostgreSQL
removes that constraint entirely while remaining just as self-hostable as SQLite was.

## Goals

- Replace SQLite with PostgreSQL as the only supported database.
- Replace `Database.EnsureCreated()` with real, versioned EF Core Migrations.
- Keep self-hosted deployment to "one command" via `docker-compose` (app + Postgres together).
- Ship a Bicep template that provisions everything needed to run dotMARC on Azure: App Service,
  Postgres Flexible Server, and Key Vault for secrets — deploying it is the user's own action, not
  something performed as part of this work.
- Add CI/CD (GitHub Actions) publishing the container image to GHCR and Docker Hub, matching the
  existing pattern already used by the sibling `psatool-busybar-agent` project.
- Replace the temp-file-SQLite test pattern with `Testcontainers.PostgreSql`, so existing tests
  that verify real unique-constraint/idempotency behavior keep exercising a real Postgres engine.

## Non-goals

- Actually provisioning any Azure resources. The Bicep template and its documentation are the
  deliverable; running `az deployment group create` against a real subscription is the user's own
  action, the same way registering the two Entra apps was documented but never performed here.
- Supporting both SQLite and PostgreSQL simultaneously. Full replacement, per the earlier design
  discussion's YAGNI reasoning — two EF Core providers and two migration-compatible SQL dialects is
  real complexity for a self-hosting story Postgres already covers on its own.
- VNet integration / private Postgres access. Public access with an "Allow Azure services" firewall
  rule is the v1 choice; a fully private network topology is a documented possible future step, not
  built here.
- SQL Server or MySQL as alternative providers — PostgreSQL only.

## Database provider swap

- `Microsoft.EntityFrameworkCore.Sqlite` → `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3.
- Remove the `SQLitePCLRaw.bundle_e_sqlite3` version override added to work around
  [CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) — moot once SQLite is gone.
- `DotMarcDbContext`'s `options.UseSqlite(...)` → `options.UseNpgsql(...)`.
- The entity model itself (`Domain`, `Report`, `ReportRecord`, `ParseFailure`, the unique indexes
  on `(DomainId, ReportingOrg, ReportId)` and `ParseFailure.GraphMessageId`, the enum-to-string
  conversions) is untouched — only the provider and connection string format change.
- `ConnectionStrings:DotMarc` changes from a SQLite file path to a standard Npgsql connection
  string (`Host=...;Database=...;Username=...;Password=...`).

## Real EF Core Migrations

- Replace the `Database.EnsureCreated()` call in `Program.cs` with `Database.Migrate()`.
- Add one checked-in `InitialCreate` migration (`dotnet ef migrations add InitialCreate`) capturing
  the full current schema as of this change. No data-preserving migration logic is needed — nothing
  has ever been deployed anywhere (per the earlier explicit choice to keep this project local for
  now), so there's no existing data to carry forward.
- This directly resolves a gap the project's own final whole-branch review flagged: "any
  post-deployment schema change... will need a real migration path against an existing volume."

## Self-hosted deployment

- New `docker-compose.yml` at the repo root: the existing `dotMARC` app image (built from
  `src/DotMarc/Dockerfile`, unchanged) plus a `postgres:18` service with a named volume for
  Postgres's own data directory.
- Still "one command to self-host" — `docker compose up` replaces the current single `docker run`
  instruction in the README, with Postgres connection details wired between the two services via
  compose's own environment variables.
- The app's Dockerfile itself needs no changes — Npgsql is a managed .NET driver with no native
  client tooling to bake into the image, unlike SQLite's native library.

## Azure deployment

### Bicep template

A new `infra/main.bicep` (plus supporting parameter files) provisioning:

- **App Service Plan** (Linux) + **Web App for Containers**, `Always On` enabled (required — the
  background poller stops if the app idles out) and WebSockets enabled (required for Blazor
  Server's SignalR connection). The container image reference is a parameter, defaulting to the
  GHCR tag the release workflow publishes, so it can be pointed at Docker Hub or a specific version
  instead.
- **Azure Database for PostgreSQL Flexible Server** (PostgreSQL 18, the current version on Azure),
  public network access with an "Allow Azure services" firewall rule — no VNet, per the earlier
  networking decision.
- **Key Vault**, with the Web App's system-assigned managed identity granted access. `Graph:ClientSecret`,
  `EntraId:ClientSecret`, and the Postgres connection string are stored here; App Service settings
  reference them via `@Microsoft.KeyVault(SecretUri=...)` rather than holding the plaintext values
  themselves.
- Non-secret configuration (`Graph:ClientId`, `Graph:TenantId`, `Graph:MailboxAddress`,
  `EntraId:TenantId`, `EntraId:ClientId`) as regular App Service application settings, matching the
  existing `Graph__*`/`EntraId__*` env-var convention already documented for Docker.

### README

A new Azure deployment section: what the template provisions, how to run it
(`az deployment group create` against a resource group, or an ARM/Bicep "Deploy to Azure" button),
and — since the template provisions empty Key Vault secret entries but doesn't populate them —
explicit steps for setting the three secret values into Key Vault after deployment (the Entra app
registration steps themselves remain manual, same as the existing Docker documentation).

## CI/CD

Three new GitHub Actions workflows, mirroring `psatool-busybar-agent`'s existing pattern exactly
(same job structure, same conditional-Docker-Hub logic, same multi-arch build):

- **`ci.yml`** — `dotnet build`/`dotnet test` against `dotMARC.sln` on push to `main` and on pull
  requests.
- **`publish.yml`** — on push to `main`: multi-arch (`linux/amd64,linux/arm64`) build, pushed to
  GHCR unconditionally (tags `edge` and the commit SHA), and to Docker Hub
  (`homotechsual/dotmarc`) only if `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` repo secrets are present.
- **`release.yml`** — on a `v*.*.*` tag: runs tests, builds and pushes versioned/major.minor/major/
  `latest` tags to both registries (Docker Hub credentials are required, not optional, for a tagged
  release — matching `psatool-busybar-agent`'s existing "require Docker Hub for tagged releases"
  check), verifies the Docker Hub tags actually landed, then creates a GitHub release listing both
  image locations.

## Testing

- `Testcontainers.PostgreSql` 4.13.0 replaces the current pattern (`UseSqlite($"Data Source={tempFile}")`
  per test) across `DotMarcDbContextTests`, `PollingServiceTests`, and `ProgramDiValidationTests`.
- Each test class starts a real, throwaway Postgres container (via Testcontainers' own lifecycle
  hooks — `IAsyncLifetime` in xUnit) and runs migrations against it, rather than relying on
  `EnsureCreated()`'s schema-from-model behavior — this keeps the tests honest about what
  production actually does (`Database.Migrate()`), not a shortcut only tests use.
- This requires Docker to be available wherever tests run — already true for this project (the
  existing Docker smoke-tests each task's implementer has run have consistently found Docker
  available in this environment).
- The specific behaviors these tests exist to prove (the unique-constraint-based idempotency and
  duplicate-prevention logic from the final whole-branch review's fix wave) continue to be
  verified against a real relational engine, not a weaker stand-in like EF Core's `InMemory`
  provider, which doesn't enforce unique constraints at all.

## Migration path for existing tests

The existing tests that construct a `DotMarcDbContext` directly against a temp SQLite file need to
be rewritten against Testcontainers' Postgres connection string instead — this is a genuine rewrite
of those tests' setup/teardown, not a drop-in replacement, since Testcontainers' container lifecycle
(start once per test class, not per test method, for reasonable test suite speed) is a different
shape than "create and delete a temp file per test."
