# Public API design

**Release:** dotMARC 0.8.0
**Status:** approved in brainstorming, awaiting spec review

## Purpose

Let MSP staff and their tools talk to dotMARC without a browser: scripted bulk work, custom client reporting, and integrations dotMARC doesn't have built in. Keys respect the existing role model, including group scoping, so a key can be read-only or limited to certain clients.

## Scope

**In:** read domains, domain health, report summaries, groups, tags and alerts; add a domain; import domains; set a domain's groups, tags and monitoring; acknowledge alerts.

**Out (UI only for now):** deleting or reordering domains, managing groups, tags, roles, access or settings, DNS push, MTA-STS settings, logs and the audit log.

## Approach

Minimal-API endpoint groups under `/api/v1`, authenticated by a dedicated API-key scheme that produces the same permission and scoped-group claims as a signed-in user. The existing per-permission authorization policies and group scoping therefore apply unchanged, and the UI and API cannot drift apart on who may do what. Writes call the existing audited static services.

Rejected: MVC controllers with Swashbuckle (adds the controller stack and a third-party package for the same result); reusing the cookie scheme (tools would need interactive Entra sign-in).

## 1. API keys

### Storage

New `ApiKeys` table:

| Column | Notes |
| --- | --- |
| Id | int |
| Name | required, unique among unrevoked keys, max 100 |
| Prefix | first 12 characters of the secret (`dmk_` + 8), shown in lists to identify a key |
| Hash | SHA-256 of the full secret, hex, unique index |
| RoleId | FK to Roles, restrict delete |
| Groups | many-to-many with Groups, like `UserAccess` scoped groups |
| CreatedBy | display name of the creator, captured at creation |
| CreatedUtc, ExpiresUtc | |
| LastUsedUtc | nullable |
| RevokedUtc, RevokedBy | nullable |

The secret is `dmk_` followed by 32 random bytes in base64url. It is shown once, in the create dialog, with a copy button, and never stored in plain form. A plain SHA-256 is enough because the secret has 256 bits of entropy.

### Management

The Access page (gated by `AccessManage`) becomes three tabs, each with its own URL: **People** (`/access/people`, the default, today's access grants), **Roles** (`/access/roles`) and **API keys** (`/access/api-keys`). It lists keys (name, prefix, role, groups, created by and when, expires, last used, status), and has:

- **Create:** name, role, groups (when the role is scopable, as for user grants), and expiry of 30, 90, 180 or 365 days (default 90). Audited as `ApiKeyCreated`.
- **Revoke:** with confirmation. Audited as `ApiKeyRevoked`. Revoked keys stay listed, greyed, for the record.

There is no editing of a key's role, groups or expiry. Changing a key means creating a new one and revoking the old, so a key's power never silently grows.

**A key can never hold `AccessManage`.** Roles that include it are left out of the create dialog's role list and refused by the service. If a role used by a key later gains `AccessManage`, the auth handler drops that permission from the key's claims, so keys cannot mint keys or change grants.

Deleting a role that unrevoked keys use is refused, as it already is for roles held by users.

### Authentication

`ApiKeyAuthenticationHandler`, scheme name `ApiKey`:

1. Reads `Authorization: Bearer dmk_...`. A missing or malformed header is no result, which leads to a challenge.
2. Hashes the secret and looks up the key. An unknown, revoked or expired key fails.
3. Builds a principal with `dotmarc:permission` claims from the role's permissions (minus `AccessManage`), `dotmarc:scoped-group` claims from the key's groups (only when the role is scopable), a `dotmarc:api-key-id` claim and a name claim of the key's name. The claim building is shared with `UserAccessClaimsTransformation` so both produce identical claims for identical role and groups.
4. Updates `LastUsedUtc` when it is null or more than a minute old.

A challenge or forbid returns RFC 7807 problem+json with 401 or 403, never a redirect.

The `/api/v1` route group requires the `ApiKey` scheme only. Its policies are built with `AuthenticationSchemes = ApiKey`, so a browser cookie on an API request is ignored. This keeps writes free of CSRF concerns. The UI never accepts the `ApiKey` scheme.

### Audit

`AuditActorKind.ApiKey` and `AuditActor.ForApiKey(int keyId, string keyName, string createdBy)`. The entry reads as "API key 'Halo sync' (created by Jo Smith)". `AuditActor.FromPrincipal` returns the API key actor when the principal carries `dotmarc:api-key-id`, so services that take the actor from the principal need no change. The audit log and its export show the key's actor name; the entry's actor kind is stored as `ApiKey`.

New `AuditActions`: `ApiKeyCreated`, `ApiKeyRevoked`, both in `AuditActions.All`.

### Expiry warning

The monitor cycle checks for unrevoked keys expiring within 14 days and raises an `ApiKeyExpiring` alert per key through the existing alert channels (not tied to a domain; the alert names the key and its expiry). It resolves when the key is revoked or has expired. An expired key raises nothing further. The Access page's API keys tab also shows an "expires soon" chip.

## 2. Endpoints

All under `/api/v1`, JSON in and out, camelCase. Each endpoint requires the same permission policy as its UI equivalent.

| Method | Path | Permission | Behaviour |
| --- | --- | --- | --- |
| GET | `/domains` | DomainsView | Paged: `page` (default 1), `pageSize` (default 50, max 200). Filters: `group` (id), `tag` (id), `monitored` (bool). Items: id, name, monitored, groups (id, name), tags (id, name), lastReportReceivedUtc, passRate (30 days, null without reports). Response: `{ items, page, pageSize, totalCount }`. Ordered as in the UI (SortOrder, then name). |
| GET | `/domains/{id}` | DomainsView | The list item plus `health`: for each of dmarc, spf, dkim, mx, tlsrpt, mtaSts and dmarcAuthorization, `{ status, checkedUtc, detail }`; `dmarcPolicy` (policy, subdomainPolicy, percent); `dnsProvider` (provider, zone). |
| GET | `/domains/{id}/reports/summary` | DomainsView | `days` (1 to 30, default 30). Returns totalVolume, passRate, reasonBreakdown, and the top 20 sources by volume (ip, volume, spf, dkim, disposition), all from `DomainStatistics`. |
| POST | `/domains` | DomainsAdd | `{ "name": "contoso.com" }` through `AddDomainAsync`. 201 with the domain and a `Location` header. An existing domain is 409. An invalid name is 400. A scoped key is refused with 403, because a new domain is in no group and so would be outside its scope. |
| POST | `/domains/import` | DomainsAdd | `{ "existingDomains": "skip", "add" or "match", "unknownNames": "skip" or "create", "domains": [{ "name", "groups": [names], "tags": [names], "monitored" }] }`, max 500 rows. `existingDomains` maps to `ExistingDomainMode` with the UI's meanings (skip them; add groups and tags, where `-Name` removes one; make groups and tags match), default `skip`; `unknownNames` defaults to `skip`. Builds an `ImportTable` and runs `DomainImportPlanner` and `DomainImportService.ApplyAsync`, so validation and permissions match the UI import (creating groups or tags still needs GroupsAdd or TagsAdd). `?dryRun=true` returns the plan without applying it. The response lists per-row outcomes, notes and unknown names. A scoped key is refused with 403 for the same reason as POST `/domains`. |
| PUT | `/domains/{id}/groups` | DomainsEdit | `{ "groupIds": [...] }` through `SetDomainGroupsAsync`. 204. |
| PUT | `/domains/{id}/tags` | DomainsEdit | `{ "tagIds": [...] }` through `SetDomainTagsAsync`. 204. |
| PUT | `/domains/{id}/monitoring` | DomainsEdit | `{ "monitored": true }` through `SetMonitoredAsync`. 204. |
| GET | `/groups` | GroupsView | id, name, domainCount. |
| GET | `/tags` | TagsView | id, name, color, domainCount. |
| GET | `/alerts` | AlertsView | `status` = open (default) or all, plus paging as for domains. Items: id, type, typeName, subject, domain (id, name; null when the subject isn't a domain the key can see), severity, title, message, raisedUtc, resolved, resolvedUtc, acknowledgeable. dotMARC records no acknowledged-by; acknowledging resolves the alert. |
| POST | `/alerts/{id}/acknowledge` | AlertsManage | Through `AlertAcknowledgement.AcknowledgeAsync`, so a PSA ticket closes exactly as from the UI. 200 with `{ ticketClosed }`, or 409 when the alert is already closed or isn't a policy or nameserver alert. |

### Rules for every endpoint

- **Scoping.** A scoped key sees only domains in its groups, and only those groups. A domain or alert outside its scope is 404, not 403, so the API doesn't confirm it exists. `/groups` lists only the key's groups; `/tags` shows domain counts within scope. A write that names an out-of-scope group is 403. Setting a domain's groups never removes groups outside the key's scope, matching the UI's behaviour for scoped users.
- **Health is read from stored check results.** The API never triggers live DNS lookups.
- **Errors** are RFC 7807 problem+json (`ProblemDetails`): 400 with field errors, 401, 403, 404, 409, 429. Unknown JSON properties are ignored; malformed JSON is 400.
- **Rate limit.** 120 requests per minute per key, fixed window, via ASP.NET's built-in rate limiter partitioned by key id. Over the limit is 429 with `Retry-After`.
- **OpenAPI.** `Microsoft.AspNetCore.OpenApi` serves the document at `/api/v1/openapi.json`, anonymously (it lists endpoints, not data). The API keys tab links to it.

## 3. Static OpenAPI copy

The committed file `website/data/openapi/dotmarc-api.json` is the current release's API document, for the website's Docusaurus OpenAPI plugin (which the maintainer installs and configures separately).

- **Kept accurate by a test.** `OpenApiDocumentTests` starts the app in-process, fetches `/api/v1/openapi.json` and compares it with the committed file. A difference fails with "The API changed; run `scripts/update-openapi` (or set `DOTMARC_UPDATE_OPENAPI=1`) and commit the result." With that variable set, the test writes the file instead. CI and the release workflow already run the tests, so an API change can't ship with a stale file.
- **Stable output.** The JSON is written with sorted object keys and two-space indentation so diffs stay readable.
- **Versioned.** `info.version` is the project's `VersionPrefix`. `scripts/release.mjs prepare` regenerates the file after bumping the version, and `release.mjs check` fails if the file's `info.version` doesn't match the release.
- **Contents.** A `bearer` security scheme describing `dmk_` keys; a summary and description on every operation; request and response schemas with examples; and the required permission in each operation's description and an `x-dotmarc-permission` extension.

## 4. Testing

- **Auth handler:** valid key; unknown, expired and revoked keys (401); claims match a user with the same role and groups; `AccessManage` dropped from claims; a cookie on an `/api` request is ignored; an unauthenticated call gets a problem+json 401 and no redirect; `LastUsedUtc` throttling.
- **Each endpoint:** happy path; 403 without the permission; scoped-key behaviour (out-of-scope domain 404, out-of-scope group write 403, add and import 403); validation as problem+json; writes record an API key actor.
- **Key management:** secret shown once and only the hash stored; `AccessManage` roles refused; revoke works and the key stops authenticating; deleting a role in use by a key is refused.
- **Expiry alert:** raised within 14 days, resolved on revoke or expiry.
- **Rate limiter:** the 121st request in a minute is 429 with `Retry-After`.
- **OpenAPI:** the committed-copy test; the document validates as OpenAPI 3; every operation has a summary and the permission extension.
- **Audit coverage:** the existing `AuditActions.All` coverage test picks up the new actions.

## Documentation

A new docs page, `website/docs/api.mdx`, covering creating a key, authentication, scoping, rate limits, errors and a link to the interactive reference. Release notes for 0.8.0 mention the API.
