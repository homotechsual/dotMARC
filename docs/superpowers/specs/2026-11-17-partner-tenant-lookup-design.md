# Match domains to Microsoft 365 tenants and partner customers

## Goal

Show, for each monitored domain, which Microsoft Entra (Microsoft 365) tenant it belongs to, and whether that tenant is one of our own partner customers. This helps an MSP in three ways:

- **Spot strays.** A monitored domain that is not in any customer tenant we manage stands out.
- **Group by customer.** Domains can be suggested for grouping by tenant, which is how an MSP already thinks of clients. This pairs with the Dashboard's "By group" view.
- **Notice moves.** A domain that changes tenant raises an alert, because it usually means a migration nobody told us about.

It is informational. It never changes DNS, groups or tickets by itself.

## What we checked in Microsoft's documentation

These come from Microsoft Learn on 17 November 2026. Items we could not confirm are in "Open questions" below.

**Domain to tenant**
- Graph `GET /tenantRelationships/findTenantInformationByDomainName(domainName='contoso.com')` returns a `tenantInformation` object with the tenant ID.
- It needs the application permission `CrossTenantInformation.ReadBasic.All`. It needs no Entra role. It is available in the global, US Government and China clouds.

**Our partner customers**
- Graph `GET /tenantRelationships/delegatedAdminCustomers` lists customers. The application permission is `DelegatedAdminRelationship.Read.All`, and it needs admin consent. `$top` goes up to 300, and paging uses `$skipToken`.
- Graph `GET /tenantRelationships/delegatedAdminRelationships` lists GDAP relationships. Each carries the customer's tenant ID and display name and a status (active, expired, terminated and so on). It uses the same permission.
- Partner Center `Get a list of customers` supports app-only credentials (a standalone app) as well as app+user credentials. Reading a customer's custom domains or company profile needs app+user credentials and GDAP roles, so it is not usable from a background job.

## Open questions

Each needs a short test against a real partner tenant before the matching phase is built.

1. Does `findTenantInformationByDomainName` work for any domain verified in a tenant, or only a tenant's primary domain? The parameter is documented as the "primary domain name".
2. Is dotMARC's existing Graph app registration in our partner tenant? `delegatedAdminCustomers` only returns data when called against the partner tenant, so a registration elsewhere would need consent in ours.
3. Does `delegatedAdminCustomers` include customers with a reseller (CSP) relationship but no GDAP relationship? If not, the Partner Center customer list may be the better source.
4. How fast do the calls throttle when checking hundreds of domains?

## Design

### Two independent pieces

The pieces ship separately. The first is useful on its own.

**Phase 1: tenant lookup for each domain.** No partner access needed.
**Phase 2: match against our partner customers.** Needs the partner permissions.

### Data

On `Domain`:
- `TenantId` (nullable GUID as text): the tenant the domain is verified in.
- `TenantCheckedUtc`: when it was last looked up.
- `TenantLookupStatus`: `NotChecked`, `Found`, `NotInAnyTenant`, `Failed`.
- `TenantLookupDetail`: a short, actionable message when the status is `Failed` (see "Failures").

A new table `PartnerCustomers` (Phase 2), refreshed by a sync:
- `TenantId` (unique), `DisplayName`, `RelationshipStatus` (the best GDAP status for that customer, or null if the source has none), `SyncedUtc`.

A domain is a partner customer's when `Domain.TenantId` matches a `PartnerCustomers.TenantId` with an active relationship. That is computed when the Dashboard loads, not stored on the domain, so a change in the customer list shows up immediately.

### When lookups run

- When a domain is added, and whenever its DNS provider check runs. A tenant doesn't change often, so the normal check cadence is enough.
- A manual "Recheck tenant" action on the domain's page, like the existing recheck buttons.
- A domain whose result is `NotInAnyTenant` is rechecked on the slow schedule, since most domains are simply not on Microsoft 365.

### Phase 2 sync

- A background sync every 6 hours, plus a "Sync now" button on the settings page. It reads every page of `delegatedAdminCustomers` and replaces the table in one transaction, so a failed sync leaves the last good list in place.
- Customers that disappear from the source are removed, not kept as history.

### Settings

A new "Partner tenant" section in the existing integration settings:
- Off by default. Phase 1 needs only the Graph app permission `CrossTenantInformation.ReadBasic.All`.
- Phase 2 adds a toggle, "Match domains to our partner customers", with a one-paragraph explanation of the extra permission.
- A "Test connection" button that makes one call and reports exactly what is missing (see "Failures").

### Where it appears

- **Dashboard table:** a "Tenant" column showing the customer's display name when matched, the tenant ID's first eight characters when found but not ours, and a dash otherwise. It is sortable and filterable like the others and is saved in the user's saved filters.
- **Domain page, Overview:** tenant ID, customer name, relationship status and when it was checked.
- **Alerts:** a tenant change on a monitored domain raises an alert through the existing alert pipeline. Text: "contoso.com moved from tenant A to tenant B", with the date.
- **Grouping suggestions:** on the Groups page, "Create groups from customers" lists customers with their domains and creates one group per ticked customer. Nothing is created without a click.

### Failures

Every failure shows what went wrong and what to do, following the MTA-STS provisioning messages (`MtaStsProvisioningError`):

| Cause | Message says |
| --- | --- |
| 401/403 from Graph | The app needs `CrossTenantInformation.ReadBasic.All` (Phase 1) or `DelegatedAdminRelationship.Read.All` (Phase 2), and an admin must grant consent. Names the permission. |
| App registered outside the partner tenant (Phase 2) | The Graph app has to be registered in, or consented to by, the partner tenant. |
| 404 for a domain | The domain isn't verified in any Microsoft 365 tenant. Not an error, so the status is `NotInAnyTenant`. |
| 429 or 5xx | Temporary. Nothing to do, dotMARC retries. |
| Network failure | dotMARC couldn't reach Microsoft. Usually temporary. |

The full exception, including Microsoft's request ID, goes to the server log. The Azure error body and headers are never shown.

## Out of scope

- Changing anything in a customer tenant. Everything here is read-only.
- Reading a customer's accepted domains through Partner Center. That needs a signed-in user with GDAP roles.
- Sovereign cloud login hosts beyond what Graph's national-cloud endpoints give us. They would be a follow-up if asked for.
- Matching by MX records. The existing mail service detection already says "Microsoft 365", and a tenant ID is more precise.

## Testing

- Unit tests for the matching rule, the tenant-change detection and the failure messages, using fakes for the Graph calls.
- Database tests (the existing Postgres fixture) for the migration, the sync's replace-in-a-transaction behaviour, and a failed sync leaving the old list intact.
- A manual run against a real partner tenant answers the open questions before Phase 2 is built.

## Phasing

1. **Phase 1:** `Domain` columns and migration, the lookup, the Dashboard column, the domain page and the settings test button. This answers open question 1 on its own.
2. **Phase 2:** the partner customer sync, the match, the "not ours" filter, and the tenant-change alert. Built after open questions 2 and 3 are answered.
3. **Phase 3 (optional):** group suggestions from customers.
