# DNS Push Secret Storage Design

## Overview

DNS provider push (Cloudflare, Azure DNS) currently registers its OAuth client credentials
(`CloudflareDns__ClientId`/`ClientSecret`, `AzureDns__TenantId`/`ClientId`/`ClientSecret`) as
deploy-time environment variables — the same pattern as the mailbox/dashboard Entra app
registrations, which is right for those (load-bearing for the whole app booting at all) but wrong
here: DNS push is an optional, admin-toggleable add-on, structurally the same shape as the HaloPSA
PSA integration, not a boot-time dependency. This design moves it onto the same DB-backed,
admin-editable pattern HaloPSA just established (see
`2026-09-02-halopsa-integration-design.md`), and generalizes that integration's secret-store code
so a third near-identical implementation isn't needed.

This also resolves a real, freshly-discovered documentation gap: this session found that
`CloudflareDns__*`/`AzureDns__*` weren't even wired up correctly on either deployment target (fixed
in commit `3c3e1cf`), and that Azure operators had no documented way to set new deploy-time secrets
without re-running the whole Bicep template. Moving to DB-backed config eliminates that problem
outright — there's nothing left to document about "how do I set an env var after deployment,"
because it's no longer an env var.

## Goals

- Cloudflare and Azure DNS OAuth client credentials configured through a new admin UI page,
  editable at runtime, no redeploy needed to set or rotate them.
- Generalize the HaloPSA secret-store abstraction (`IHaloSecretStore` → `ISecretStore`, keyed) so
  it serves all three secrets (HaloPSA, Cloudflare DNS, Azure DNS) rather than duplicating the
  Postgres-encrypted-vs-Key-Vault pattern a second and third time.
- One deployment-wide choice for where secrets live (Key Vault vs. Postgres), not a per-integration
  toggle — `enableHaloPsaKeyVaultWrite` becomes the generic `enableKeyVaultWrite`.
- Clean break from the `CloudflareDns__*`/`AzureDns__*` env vars: no read-as-seed fallback. Nothing
  is deployed yet with real values in them (the env-var path was only fixed and documented this
  same session), so there's no migration audience to protect.

## Non-goals

- Anything about the *ephemeral, per-user* OAuth push flow itself — the Authorization Code + PKCE
  exchange, "nothing about the access token is ever persisted." That non-goal from the original DNS
  provider push design (`2026-09-01-dns-provider-push-design.md`) is untouched. Only the
  deployment's own registered OAuth *client* credentials move to DB storage; no end-user push-time
  token is ever persisted, before or after this change.
- Moving the Graph/EntraId mailbox and dashboard app secrets to this pattern. Those are load-bearing
  for the app to function at all (sign-in, report ingestion) — deploy-time env var config stays
  right for them. The dividing line is "can the app usefully run without it," not "is it a secret."
- A generic secret-store framework beyond these three keys. `ISecretStore` is scoped to what these
  three integrations need; a fourth integration's storage gets its own decision when it's built.

## Generalized secret store

```csharp
public interface ISecretStore
{
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
}
```

Keys are dot-namespaced business names: `"HaloPsa.ClientSecret"`, `"CloudflareDns.ClientSecret"`,
`"AzureDns.ClientSecret"`.

**`DatabaseSecretStore`** (default/fallback): a new shared table,

```csharp
public sealed class EncryptedSecret
{
    public required string Key { get; set; }       // primary key
    public required string ProtectedValue { get; set; }
}
```

replacing `HaloPsaSettings.ProtectedClientSecret`'s dedicated column. One `IDataProtector` for the
whole store (a single purpose string, `"DotMarc.Notifications.EncryptedSecret.v1"`) rather than one
protector per secret type — Data Protection's purpose string is about protector identity/versioning,
not which row it's for; the `Key` column already scopes rows. Since nothing is deployed with a real
value in `HaloPsaSettings.ProtectedClientSecret` yet, the migration adds the `EncryptedSecrets` table
and drops that column — no data migration needed.

**`KeyVaultSecretStore`**: derives the Key Vault secret name from `key` by replacing `.` with `-`
(`"HaloPsa.ClientSecret"` → `HaloPsa-ClientSecret`, the exact name already in production use today;
`CloudflareDns-ClientSecret`/`AzureDns-ClientSecret` match the empty placeholders this session
provisioned in Bicep, which are removed — the app creates them itself on first save, same as
`HaloPsa-ClientSecret` already does, never provisioned empty).

**Selection stays one deployment-wide choice**, not per-integration: `KeyVault:VaultUri` configured
→ `KeyVaultSecretStore` for all three secrets; otherwise `DatabaseSecretStore` for all three. The
Bicep param/role rename from `enableHaloPsaKeyVaultWrite`/`haloPsaKeyVaultWriteRole` to
`enableKeyVaultWrite`/`keyVaultWriteRole` reflects that — the RBAC permission itself
(`secrets/setSecret/action`, vault-wide) doesn't change, it already covers arbitrary secret names.

**Existing HaloPSA call sites need updating for the new keyed shape**, not just the two new
integrations — this is generalizing already-shipped code, not adding a parallel path:
`HaloPsaSettingsService.SaveAsync`'s `secretStore.SetClientSecretAsync(newClientSecret, ct)` call
becomes `secretStore.SetSecretAsync("HaloPsa.ClientSecret", newClientSecret, ct)`;
`HaloPsaClient`'s private `SendAsync` helper's `_secretStore.GetClientSecretAsync(ct)` call becomes
`_secretStore.GetSecretAsync("HaloPsa.ClientSecret", ct)`; every `IHaloSecretStore`-typed
constructor parameter and DI registration (`HaloPsaSettingsService`'s caller in
`AlertsSettings.razor`, `HaloPsaClient`, `Program.cs`'s `DatabaseHaloSecretStore`/
`KeyVaultHaloSecretStore` registration, `AlertsSettings.razor`'s injected `SecretStoreAccessor`)
retypes to `ISecretStore`.

## Settings entities and services

Two new singleton entities, seeded via migration `HasData` (same pattern as
`NotificationSettings`/`HaloPsaSettings`):

```csharp
public sealed class CloudflareDnsSettings
{
    public int Id { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
}

public sealed class AzureDnsSettings
{
    public int Id { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
}
```

Two new static services, `CloudflareDnsSettingsService`/`AzureDnsSettingsService`, each with
`GetAsync(context, ct)` / `SaveAsync(context, secretStore, updated, newClientSecret, ct)` —
identical shape to `HaloPsaSettingsService`, same "`ClientSecretConfigured` set to `true` if and
only if a non-blank `newClientSecret` was actually provided and stored, existing value otherwise
untouched" invariant.

## Provider interface changes

`CloudflareDnsPushProvider`/`AzureDnsPushProvider` currently take `IOptions<T>` injected once at
construction. Settings are now DB-backed and admin-editable at runtime, so both providers need to
read fresh per call — the same reasoning that already makes `HaloPsaClient` take `HaloPsaSettings`
per-call rather than caching it at construction. Both switch to constructor-injected
`IDbContextFactory<DotMarcDbContext>` + `ISecretStore`, fetching settings inside each method.

This forces two `IDnsPushProvider` interface methods to become async. Confirmed clean via a grep of
every `IsConfigured` call site before committing to this: it is never read from Razor markup
directly (the "Push via your DNS provider" button always renders under the `DomainsEdit` policy;
the configured-provider check happens only inside the async click handler, falling back to a
Snackbar warning if none match) — so this is a mechanical signature change, not a markup
restructure.

```csharp
public interface IDnsPushProvider
{
    string ProviderKey { get; }
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);
    Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default);
    Task<DnsPushResult> ExchangeAndPushAsync(string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken);
}
```

The five existing call sites (`Program.cs`'s two `/dns-push/{provider}/start`/`callback`
endpoints, `DomainDetail.razor` ×2, `ManageMtaSts.razor` ×1) each change from a synchronous
`.SingleOrDefault(p => p.ProviderKey == x && p.IsConfigured)` LINQ filter to a small `foreach` loop
awaiting `IsConfiguredAsync` — every one of the five already lives inside an `async` method, so this
is mechanical, no structural change to any of them.

## New settings page and permission

A new page, `/dns-push/settings`, following `AlertsSettings.razor`'s exact structural pattern (two
independent `MudPaper` sections on one page, one per provider, each with its own state/handlers)
rather than a page per provider — mirrors how Alert settings already bundles Teams + generic
webhook delivery on one page:

- **Cloudflare section**: Client ID field, write-only Client Secret field (shows
  "configured"/"not configured", same convention as HaloPSA's), Save button.
- **Azure DNS section**: Tenant ID, Client ID, write-only Client Secret, Save button.

New permission, `DnsPushManage` — a single permission, no View/Manage split (unlike
`MtaStsView`/`MtaStsManage`, there is no separate "view" audience for OAuth client credentials;
this is pure admin config, matching `AccessManage`'s single-permission shape). Gates only this new
page. The existing per-target permission checks (`DomainsEdit` for the DMARC/TLSRPT push target,
`MtaStsManage` for the MTA-STS target) that already gate the *push button* itself are a separate,
unrelated concern and are unchanged by this design.

## Infra unwind

This substantially reverts this session's DNS-secrets-fix commit (`3c3e1cf`), since that fix wired
the env-var path this design is replacing:

- `infra/main.bicep`: remove `cloudflareDnsClientId`/`azureDnsTenantId`/`azureDnsClientId` params,
  remove the `CloudflareDns-ClientSecret`/`AzureDns-ClientSecret` empty-provisioned Key Vault
  secrets and their container-app `secretRef`s, remove the five `CloudflareDns__*`/`AzureDns__*`
  container env var entries. Rename `enableHaloPsaKeyVaultWrite` → `enableKeyVaultWrite`, and
  `haloPsaKeyVaultWriteRole`/its role assignment → `keyVaultWriteRole`/its assignment (permission
  scope unchanged — vault-wide `secrets/setSecret/action` already covers every key this store
  writes, Halo or DNS push).
- `infra/main.parameters.json`: remove the three DNS params, keep the renamed flag.
- `docker-compose.yml`: remove the five `CloudflareDns__*`/`AzureDns__*` lines added this session.

## Docs

- `getting-started.mdx`: the "DNS provider push (optional)" section's OAuth app registration
  walkthroughs (creating the Cloudflare OAuth client, registering the third Azure DNS Entra app)
  are unchanged — that setup still happens once, outside dotMARC, exactly as documented today. Only
  the "how you tell dotMARC about it" part changes: replace the "Set: `CloudflareDns__ClientId`..."
  tables and the Docker-Compose-vs-Azure shell-variable split with a pointer to the new
  `/dns-push/settings` page. This *simplifies* the doc — config is now uniform regardless of
  deployment target, no shell-variable-name table needed at all.
- `deploy-to-azure.mdx`: delete the "Optional: DNS provider push secrets" subsection entirely
  (nothing left to document there). Broaden "Optional: HaloPSA Key Vault storage" into a generic
  "Optional: Key Vault-backed secret storage" section covering all three secrets under the renamed
  `enableKeyVaultWrite` flag.
- `permissions-and-access.mdx`: add `DnsPushManage` to the Roles section, alongside the existing
  `MtaStsView`/`MtaStsManage`/`AlertsView`/`AlertsManage` list.

## Testing

Following this codebase's established split between pure logic and unmockable external I/O, same
pattern as the HaloPSA integration's own testing section:

- `DatabaseSecretStore`/`KeyVaultSecretStore`: round-trip tests against the new keyed shape (set
  then get under one key; a different key or a never-set key returns null; a value protected under
  a different Data Protection key ring returns null rather than throwing) — direct ports of the
  existing `DatabaseHaloSecretStoreTests`/`KeyVaultHaloSecretStoreTests`, generalized to take a
  `key` parameter instead of being HaloPSA-specific.
- `CloudflareDnsSettingsService`/`AzureDnsSettingsService`: same `ClientSecretConfigured` round-trip
  tests as `HaloPsaSettingsServiceTests`, using a **fresh** `DotMarcDbContext` for every read-back
  (the exact EF Core identity-map pitfall the HaloPSA plan's Task 4 got wrong on its first pass —
  do not repeat it here).
- `CloudflareDnsPushProvider`/`AzureDnsPushProvider`: existing tests (if any — check first) updated
  for the new constructor shape and async interface; `IsConfiguredAsync` tested against a seeded
  settings row with/without a configured secret, matching the existing sync-property tests'
  intent.
- The five `IDnsPushProvider` call sites: no new automated coverage needed beyond what a build
  failure would already catch (a missed `await` is a compile error, not a runtime one, given the
  interface method's return type changes from `bool`/`string` to `Task<bool>`/`Task<string>`).
- No automated test coverage for the new `/dns-push/settings` Razor page — established, accepted
  gap in this codebase (no Blazor component test harness), consistent with `AlertsSettings.razor`.
