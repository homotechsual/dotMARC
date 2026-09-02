# HaloPSA Integration Design

## Overview

dotMARC's generic webhook (see [Alerts](../../../website/docs/alerts.mdx)) is one-way: it tells
an external system an alert fired and nothing more. For an MSP running HaloPSA, that means a
ticket someone has to remember to close by hand, and an alert that stays open in dotMARC even
after the ticket's been dealt with. This design adds a HaloPSA connector that closes that loop:
a dotMARC alert opens a Halo ticket against the right client, a dotMARC-side resolve closes the
ticket, and a tech closing the ticket directly in Halo resolves the alert back.

This is the first PSA connector, not the only one ever intended (Autotask and Gorelo are the
other two named), so `AlertingService`'s coupling to Halo is kept behind a couple of narrow
interfaces. A second connector should be a new class implementing those, not a rewrite of
alerting itself.

## Goals

- Create a Halo ticket when a dotMARC alert fires, routed to the correct Halo Client via the
  domain's Group (or a per-domain override).
- Close the Halo ticket automatically when the underlying dotMARC alert resolves.
- Resolve the dotMARC alert automatically when a tech closes the ticket directly in Halo, via
  Halo's own outbound webhook hitting a new dotMARC endpoint.
- Store the Halo API client secret properly: the existing Azure Key Vault when available and
  opted into, Postgres (Data-Protection-encrypted) everywhere else. Never a bare secret at rest,
  and it must survive restarts, redeploys, and multiple replicas the same way `NotificationSettings`
  already had to (see that entity's doc comment on why it moved out of `appsettings.json`).
- Keep `AlertingService`'s coupling to Halo thin enough that a second PSA connector is a new class
  behind the same interfaces, not a rewrite of alerting.

## Non-goals

- Autotask and Gorelo connectors themselves. Not building them now, just not designing against
  them being impossible later.
- Multi-PSA-per-deployment. One deployment = one organization = at most one active PSA connector,
  consistent with dotMARC's existing single-tenant-per-deployment model (see the MTA-STS hosting
  design's non-goals).
- Syncing anything beyond create/close: no comment sync, no reassignment, no priority/severity
  sync back from Halo, no attaching DMARC/TLSRPT report data to the ticket beyond the existing
  alert title/message.
- A generic multi-provider secret-store framework. `IHaloSecretStore` is scoped to this one
  credential; a future connector's credential gets its own storage decision when it's actually
  built.
- Auto-detecting which Halo instance/account to use. One Halo account per dotMARC deployment,
  configured explicitly.
- Historical backfill: alerts already open before this ships do not retroactively get a ticket.

## A note on what's confirmed vs. needs live verification

HaloPSA's REST API uses OAuth2 `client_credentials` against a per-tenant token endpoint
(`https://{account}.halopsa.com/auth/token` pattern), authenticated via an API application
created in Halo's **Configuration → Integrations → HaloPSA API**, scoped to the permissions it
needs (`edit:tickets`, `read:tickets`, `read:customers`, `read:teams`). Tickets are created via
the `Tickets` resource (internally called "Faults"). Halo supports outbound webhooks/actions
firing on ticket events including status changes, configurable from its own integrations area.

The exact JSON field names for ticket creation, the exact outbound webhook payload shape, and
whether Halo's outbound webhook config supports custom headers are not fully confirmed from
public documentation. This design does not depend on header support (the webhook secret travels
in the URL path instead — see below), and the request/response shapes for `IHaloPsaClient` are
specified in terms of what dotMARC needs, not Halo's literal wire format; mapping onto the real
API is implementation work, verified against a live Halo tenant, the same acceptance this
codebase already makes for `AzureMtaStsHostProvisioner` and the DNS-provider-push OAuth
exchanges (neither testable in CI).

## Data model

```csharp
// Group gains:
public int? HaloClientId { get; set; }

// Domain gains:
public int? HaloClientId { get; set; } // override; null means "use the Group's mapping"

// AlertEvent gains:
public string? ExternalTicketProvider { get; set; } // "HaloPSA" today; null if no ticket was created
public string? ExternalTicketId { get; set; }
```

A new singleton settings entity, following `NotificationSettings`'s exact pattern (one row,
seeded via migration `HasData`, read with `SingleAsync`):

```csharp
public sealed class HaloPsaSettings
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? AccountName { get; set; }
    public string? AuthServerUrl { get; set; }
    public string? ResourceServerUrl { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
    public int? TicketTypeId { get; set; }
    public int? DefaultPriorityId { get; set; }
    public int? ClosedStatusId { get; set; }
    public string? WebhookSecret { get; set; }
}
```

`ClientSecretConfigured` is a UI-only flag ("a secret is set, click to replace"); the actual
secret never lives in this row (or anywhere else the app can read back in plaintext) — see the
secret storage section below. `WebhookSecret` is stored plaintext, same trust level as
`NotificationSettings.TeamsWebhookUrl`/`GenericWebhookUrl` today: a bearer-token-like value
protected by being unguessable and sent only over HTTPS, not a credential granting broader access.

One migration adds `HaloPsaSettings` (plus its seed row), the `Group`/`Domain`/`AlertEvent`
columns above, and (see below) the Data Protection keys table.

## Secret storage: Key Vault with Postgres + Data Protection fallback

The Halo API client secret is materially more sensitive than anything currently admin-configured
at runtime in dotMARC (it grants API access across the whole PSA tenant), so it needs encryption
at rest, and needs to survive restarts, redeploys, and multiple replicas — the same durability
argument that already moved `NotificationSettings` out of `appsettings.json` into Postgres.

**`IHaloSecretStore`**, selected in `Program.cs` the same way `IMtaStsHostProvisioner` picks Caddy
vs. Azure — on whether a Key Vault URI is configured:

```csharp
public interface IHaloSecretStore
{
    Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default);
    Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default);
}
```

- **`KeyVaultHaloSecretStore`** (Azure, opt-in): `Azure.Security.KeyVault.Secrets.SecretClient`
  against the vault `infra/main.bicep` already provisions, under a fixed secret name. Selected
  when `KeyVault:VaultUri` is configured. The value never touches Postgres.
- **`DatabaseHaloSecretStore`** (default/fallback): protects the value with
  `IDataProtectionProvider.CreateProtector("HaloPsa.ClientSecret")` and stores the protected
  string in a new `HaloPsaSettings.ProtectedClientSecret` column (not shown above alongside the
  UI-facing fields since it's never read back through the entity the UI binds to — modeled as a
  separate internal column/table the store alone touches).

**Data Protection key persistence** (new — today there is none configured, so
`DnsPushStateProtector`'s keys already don't survive a restart/redeploy/multi-replica, tolerated
there only because that state is minutes-lived): `DotMarcDbContext` implements
`IDataProtectionKeyContext` (adds a `DataProtectionKeys` `DbSet<DataProtectionKey>`), and
`Program.cs` calls `AddDataProtection().PersistKeysToDbContext<DotMarcDbContext>()`. This fixes
the latent gap for `DnsPushStateProtector` too, as a side effect, not a separate change.

**Infra (`infra/main.bicep`)**: a new custom RBAC role granting only
`Microsoft.KeyVault/vaults/secrets/setSecret/action` on the existing vault (read is already
covered by the existing `Key Vault Secrets User` assignment, so this is the minimum delta, not a
broader get+set role), assigned to the Container App's managed identity only when a new
`enableHaloPsaKeyVaultWrite` param is `true` (default `false`, matching `enableMtaStsHosting`'s
precedent). `KeyVault__VaultUri` is set as a container app setting only when that flag is on,
doubling as both the config the app needs and the signal `Program.cs` uses to pick
`KeyVaultHaloSecretStore` over the database fallback.

## Halo API client

```csharp
public sealed record HaloClient(int Id, string Name);
public sealed record HaloTicketType(int Id, string Name);
public sealed record HaloTicketStatus(int Id, string Name);

public interface IHaloPsaClient
{
    Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default);
    Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default);
}
```

`HaloPsaClient` (typed `HttpClient`, registered `AddHttpClient<IHaloPsaClient, HaloPsaClient>()`)
takes `HaloPsaSettings` as a parameter on every call, matching `ITeamsWebhookClient`'s/
`IGenericWebhookClient`'s existing convention (the caller fetches settings once, no client makes
its own redundant DB round trip) — the one exception is the client secret itself, which isn't on
`HaloPsaSettings` at all (by design, see above); `HaloPsaClient` depends on `IHaloSecretStore`
directly to resolve it. It acquires and caches an OAuth2 token until shortly before expiry, and
issues the actual REST calls. `ListClientsAsync`/`ListTicketTypesAsync`/`ListStatusesAsync` back
the three dropdowns in Alert settings and the Group mapping screen's name-match suggestion; none
of them are on the hot alerting path.

## Client (company) mapping and ticket lifecycle

**Resolution rule** — a domain can belong to more than one Group, and `Group`/`Domain` is an
implicit EF many-to-many with no order column, so "the domain's Groups" has no natural order to
fall back on. The rule: `Domain.HaloClientId` wins if set; otherwise, of the domain's Groups that
have a `HaloClientId` set, the one with the lowest `Group.Id` (oldest-created); otherwise no
ticket is created for that alert, silently — same "unconfigured means off" behavior as an unset
`GenericWebhookUrl` today.

**Mapping UI**: Manage Groups gets a "Halo Client" column, populated from
`IHaloPsaClient.ListClientsAsync()`, pre-filled with a suggested match by case-insensitive name
equality against the Group's name — same pattern as the MTA-STS MX-hosts sync icon: a starting
point, not a save, reviewed then explicitly saved. Manage Domains gets the same field as an
override, collapsed/secondary since it's the exception path, not the common one.

**`IPsaTicketService`**, the boundary `AlertingService` calls through (kept separate from
`IHaloPsaClient` so a future second connector plugs in here, not by teaching `AlertingService`
about Halo directly):

```csharp
public interface IPsaTicketService
{
    Task CreateTicketAsync(DotMarcDbContext context, AlertEvent alert, Domain domain, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
}
```

Both take the caller's `DotMarcDbContext` (matching `NotificationSettingsService`'s existing
caller-supplied-context convention) since both `EnsureAlertAsync` and `ResolveAlertAsync` already
have one open when they'd call these — no extra `DbContextFactory` round trip inside the service.

Wiring into `AlertingService`:

- `EnsureAlertAsync`: right after the existing best-effort `_alertWebhookClient.SendAlertAsync`
  call, a new best-effort `_psaTicketService.CreateTicketAsync(context, alert, domain, ...)` call,
  same try/catch-and-log shape — a PSA outage never blocks the alert itself being recorded.
  Resolves the Halo client via the rule above; skips silently if none, or if `HaloPsaSettings` is
  not `Enabled`/fully configured. On success, writes `ExternalTicketProvider`/`ExternalTicketId`
  onto the `AlertEvent` before it's saved.
- `ResolveAlertAsync`: if the alert being resolved has an `ExternalTicketId`, a best-effort
  `_psaTicketService.CloseTicketAsync(context, alert, ...)` call before returning.

## Inbound webhook

**`POST /integrations/halopsa/webhook/{secret}`** — a new minimal API endpoint in `Program.cs`,
registered the same way as the existing `/.well-known/mta-sts*` endpoints, `.AllowAnonymous()`.
The secret travels in the path (not a header) because Halo's outbound webhook config isn't
confirmed to support custom headers. A non-matching secret returns 404, not 401, so an
unauthenticated caller can't confirm the endpoint exists at all.

Handling: parses the ticket ID and status ID from the payload, compares the status ID against
`HaloPsaSettings.ClosedStatusId`. If it matches and an unresolved `AlertEvent` exists with that
`ExternalTicketId`, marks it resolved (`IsResolved = true`, `ResolvedUtc = now`) — without calling
`CloseTicketAsync` back, the ticket is already closed on Halo's side; calling again would be a
pointless round trip. Any other status, an unknown ticket ID, or an already-resolved alert is a
silent no-op.

**Response is always 200** once the secret checks out, including no-ops and unparseable payloads
(logged as a warning, not surfaced as an error) — there's nothing a retry from Halo would fix, and
a webhook Halo believes is failing risks a retry storm.

## Error handling

Every Halo API call from the alerting path (`CreateTicketAsync`, `CloseTicketAsync`) is
best-effort: caught, logged as a warning, and never allowed to block alert creation/resolution
itself, exactly matching the existing `_alertWebhookClient.SendAlertAsync` call's error handling
in `EnsureAlertAsync`. An unmapped domain, a disabled/unconfigured `HaloPsaSettings`, or a Halo
API outage all result in "no ticket," never a failed alert.

## UI

Alert settings (`/alerts/settings`) gets a new "PSA integration" section: Enabled toggle, Account
Name, Auth Server URL, Resource Server URL, Client ID, a write-only Client Secret field (shows
"configured"/"not configured," never the value), Ticket Type / Default Priority / Closed Status
dropdowns (populated live from `IHaloPsaClient`), and a generated webhook secret with the full
callback URL shown for copying into Halo's own webhook config. Gated behind the existing
`AlertsManage` permission — this is the same "how do alerts get delivered" concern as the
Teams/generic webhook settings already there, not a new permission surface.

## Testing

Following this codebase's established split between pure logic and unmockable external I/O:

- Pure logic, unit-tested directly: the Group/Domain Halo-client resolution rule; the webhook
  payload's closed-status matching.
- `Testcontainers.PostgreSql` + `FakeHttpMessageHandler` integration tests, matching
  `AlertingServiceTests`'/`GenericWebhookClientTests`'s existing style: a ticket is created and
  its ID stored when `EnsureAlertAsync` fires for a mapped domain, and skipped for an unmapped
  one; `ResolveAlertAsync` closes the ticket when one exists; the inbound webhook resolves the
  right `AlertEvent` on a matching closed-status payload, 404s on a wrong secret, and no-ops on an
  unrelated status or unknown ticket ID.
- Not testable in CI, verified live once implemented (same acceptance already made for
  `AzureMtaStsHostProvisioner` and the DNS-provider-push OAuth exchanges): the actual OAuth2
  `client_credentials` token exchange against a real Halo tenant, real ticket create/close calls,
  and the Key Vault `SecretClient` read/write path (needs a real managed identity).

## Docs

- A new `website/docs/psa-integration.mdx`: creating the Halo API application (required scopes),
  entering credentials in Alert settings, the Group→Client mapping screen, and pointing Halo's
  outbound webhook at the generated secret URL.
- `deploy-to-azure.mdx` gets a short section on `enableHaloPsaKeyVaultWrite`.
- `alerts.mdx` gets a cross-link to the new page.
