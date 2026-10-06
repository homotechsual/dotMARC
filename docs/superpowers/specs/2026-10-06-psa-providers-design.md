# PSA providers design (HaloPSA, ConnectWise, Autotask)

**Release:** dotMARC 0.9.0
**Status:** approved in brainstorming, awaiting spec review

## Purpose

dotMARC opens a PSA ticket when an alert fires, closes it when the alert resolves, and resolves the alert when a tech closes the ticket. Today that only works with HaloPSA. MSPs on ConnectWise Manage or Datto Autotask get no tickets at all. This adds both, on a shared PSA layer that HaloPSA moves onto, so the three behave the same and a fourth PSA later is one new provider.

## Decisions made in brainstorming

- **Several PSAs at once.** Each PSA can be enabled side by side. Each Group and Domain can map to a company in each PSA, and one alert can hold a ticket in each. This covers an MSP part-way through moving PSA.
- **Close-back by polling** for ConnectWise and Autotask. No callbacks or webhooks to set up in the PSA. The same poller also checks HaloPSA tickets, as a backup to Halo's webhook, which stays.
- **Vendor identifiers: a built-in default plus an override.** ConnectWise needs a developer `clientId` and Autotask an `ApiIntegrationCode` on every request. dotMARC ships its own as constants, and each install can override them in settings. Until the dotMARC values are registered, the constants are empty and the override field is required.
- **Approach A:** a provider abstraction, with HaloPSA moved onto it. Rejected: copying the Halo pattern for each PSA (three versions of every column, page and import path) and a JSON settings blob per PSA (loses typed validation, audit field names and EF queries).

## Scope

**In:** the shared PSA layer; moving HaloPSA onto it with no change in behaviour; ConnectWise Manage and Autotask ticket create, close and close-back; a PSA settings page; company mapping per PSA on Groups, Domains and import; a shared integration test; demo fakes; docs.

**Out for 0.9.0:** ConnectWise callbacks and Autotask webhooks; syncing ticket notes back; priority by alert type; ticket rules per PSA; moving tickets between PSAs.

## 1. Data model

### PSA identity

`PsaKind` enum: `HaloPsa`, `ConnectWise`, `Autotask`, stored as a string. Existing alert data uses the string `HaloPSA`; the migration maps it to `HaloPsa`.

### Settings, one row per PSA

Each PSA keeps a typed, singleton settings row seeded by its migration, with secrets in `ISecretStore`, the same pattern as `HaloPsaSettings` today.

- **`HaloPsaSettings`:** unchanged, including its webhook secret.
- **`ConnectWiseSettings`:**
  - Connection: `Enabled`, `SiteUrl` (for example `api-eu.myconnectwise.net`), `CompanyId` (the login company), `PublicKey`, private key in the secret store (`PrivateKeyConfigured` on the row), `ClientIdOverride` (nullable).
  - Ticket defaults: `BoardId`, `StatusId` (new ticket status), `TypeId` (nullable), `PriorityId`, `ClosedStatusId`, each with a display-name column beside it, as Halo does.
- **`AutotaskSettings`:**
  - Connection: `Enabled`, `Username`, secret in the secret store (`SecretConfigured` on the row), `IntegrationCodeOverride` (nullable). The zone URL is looked up from the username on first use and cached in memory.
  - Ticket defaults: `QueueId`, `TicketTypeId`, `IssueTypeId` (nullable), `PriorityId`, `ClosedStatusId` (defaults to 5, Complete), each with a display-name column.
- **Vendor identifier constants:** `ConnectWiseSettings.DefaultClientId` and `AutotaskSettings.DefaultIntegrationCode`. The effective value is the override if set, otherwise the default. Saving with Enabled on and no effective value fails validation.

### `PsaCompanyLink` (new)

Replaces `Group.HaloClientId` and `Domain.HaloClientId`.

| Column | Notes |
| --- | --- |
| Id | int |
| Psa | `PsaKind`, string |
| GroupId | nullable FK, cascade delete |
| DomainId | nullable FK, cascade delete |
| CompanyId | string (Autotask IDs are large numbers, ConnectWise and Halo use ints) |
| CompanyName | string, display only |

A check constraint requires exactly one of `GroupId` and `DomainId`. Unique on (`Psa`, `GroupId`) and on (`Psa`, `DomainId`). The migration copies every non-null Halo client ID into a link (with the name taken as the ID as text where none is known, refreshed next time the Groups page loads Halo's list), then drops both columns.

### `AlertTicket` (new)

Replaces `AlertEvent.ExternalTicketProvider` and `AlertEvent.ExternalTicketId`.

| Column | Notes |
| --- | --- |
| Id | int |
| AlertEventId | FK, cascade delete |
| Psa | `PsaKind`, string |
| TicketId | string |
| IsOpen | bool |
| CreatedUtc | timestamp |
| LastCheckedUtc | nullable timestamp |

Unique on (`AlertEventId`, `Psa`). Index on (`Psa`, `TicketId`) for the Halo webhook and the poller. The migration moves each existing pair across, with `IsOpen` = the alert is unresolved, then drops the two columns.

### Ticket rules

`AlertTicketRule` is unchanged and shared by all PSAs. Its doc comment stops naming HaloPSA. The deciding Group is worked out per PSA: none if the Domain has its own link for that PSA (global rules apply), otherwise the lowest-ID Group of the Domain linked in that PSA. A domain can therefore take its Halo rules from one Group and its ConnectWise rules from another. `HaloClientResolver` becomes `PsaCompanyResolver` with `Resolve(domain, psa)` and `ResolveGroup(domain, psa)`; `AlertTicketPolicy.ShouldCreateTicket` takes the `PsaKind`.

### Audit

- New actions: `settings.connectwise.saved`, `settings.autotask.saved`, `connectwise.integration_tested`, `autotask.integration_tested`, `autotask.zone_cleared`.
- `domain.psa_company_changed` and `group.psa_company_changed` replace the Halo client-changed actions for new entries, with the PSA named in the change detail. The old action names stay in the label table so existing entries still read correctly.
- The ConnectWise private key and Autotask secret are recorded with `.Secret(...)`, as the Halo secret is.

### API

`ApiAcknowledgement` becomes `ApiAcknowledgement(bool TicketClosed, int TicketsClosed, int TicketsFailed)`. `TicketClosed` keeps its meaning: true when no ticket failed to close. The OpenAPI copy is refreshed.

## 2. Ticket flow

### Provider contract

```csharp
public interface IPsaProvider
{
    PsaKind Kind { get; }
    Task<bool> IsReadyAsync(DotMarcDbContext context, CancellationToken cancellationToken);   // enabled and credentials present
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken);
    Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken);
    Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken);
    Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken);
}

public sealed record PsaCompany(string Id, string Name);
public sealed record PsaTicketRequest(string CompanyId, string DomainName, string AlertType, string Title, string Message);
public enum PsaTicketState { Open, Closed, Missing }
```

Each provider reads its own settings row. Pick-lists (boards, queues, statuses, priorities and so on) stay on each PSA's own client (`IHaloPsaClient`, `IConnectWiseClient`, `IAutotaskClient`), because they differ per PSA and only that PSA's settings tab uses them. All registered providers are resolved as `IEnumerable<IPsaProvider>`.

### Creating tickets

`IPsaTicketService.CreateTicketsAsync(context, alert, ct)` runs, for each ready provider in turn:

1. Resolve the company with `PsaCompanyResolver.Resolve(domain, psa)`. No company: skip this PSA.
2. `AlertTicketPolicy.ShouldCreateTicket(alertType, domain, psa, rules)`. False: skip.
3. Dedup: an earlier unresolved alert for the same domain and type with an open `AlertTicket` in this PSA means skip.
4. Create the ticket and add an `AlertTicket` row with `IsOpen = true`.

Each PSA is in its own try/catch and logged separately. One PSA failing never stops the others, and ticket failures never stop the alert being recorded or sent to Teams, Slack or the webhook.

### Closing tickets when the alert resolves

`CloseTicketsAsync(context, alert, ct)` returns `PsaCloseResult(int Closed, int Failed)`. For each open `AlertTicket` of the alert: close it in the PSA; on success set `IsOpen = false`; on failure leave it open and log. The note is "Resolved automatically by dotMARC." Both automatic recovery in `AlertingService` and acknowledgement use it. `AcknowledgeOutcome` carries the counts so the Alerts page and the API can report them.

### `PsaTicketPoller`

A hosted background service. Interval: 5 minutes, from `Psa:PollIntervalMinutes` in configuration.

For each open `AlertTicket`, grouped by PSA, skipping PSAs that aren't ready:

| Alert | PSA says | Action |
| --- | --- | --- |
| Unresolved | Closed | Resolve the alert, set `IsOpen = false`, close its other open tickets, audit "Resolved by ticket closed in <PSA>" |
| Unresolved | Missing | Set `IsOpen = false`, leave the alert |
| Unresolved | Open | Set `LastCheckedUtc` |
| Resolved | (not asked) | Retry `CloseTicketAsync`; on success set `IsOpen = false` |

Resolving through the poller shares one code path with the Halo webhook (`PsaTicketClosure.ResolveFromTicketAsync`), so both produce the same alert state and audit entry. A PSA that fails repeatedly backs off (doubling, capped at 1 hour), logs once per backoff step rather than every cycle, and resets on the first success. One PSA failing never stops the others.

### Halo webhook

Unchanged, except it finds the alert through `AlertTicket` (`Psa = HaloPsa`, `TicketId`) and resolves through `PsaTicketClosure`.

### What "closed" means

- **HaloPSA:** status equals the configured closed status (today's rule).
- **ConnectWise:** `closedFlag` is true, or status equals the configured closed status. A 404 is `Missing`.
- **Autotask:** status equals the configured closed status (default 5, Complete). No ticket returned is `Missing`.

### Provider specifics

- **ConnectWise:** base `https://{SiteUrl}/v4_6_release/apis/3.0/`. Basic auth `{CompanyId}+{PublicKey}:{PrivateKey}`, header `clientId`. Create: `POST service/tickets` with summary (title, trimmed to 100 characters), `initialDescription` (message), `company.id`, `board.id`, `status.id`, `type.id` if set, `priority.id`. Close: `POST service/tickets/{id}/notes` with the note as an internal analysis note, then `PATCH service/tickets/{id}` with a JSON Patch setting `status/id` to the closed status. Companies: `GET company/companies` with `conditions=deletedFlag=false`, paged by 1000.
- **Autotask:** zone from `GET https://webservices.autotask.net/atservicesrest/v1.0/zoneInformation?user={Username}`, cached per username. Headers `ApiIntegrationCode`, `UserName`, `Secret`. Create: `POST Tickets` with `companyID`, `title` (trimmed to 255), `description`, `queueID`, `ticketType`, `issueType` if set, `priority`, `status` (1, New). Close: `POST TicketNotes` (note type and publish set for internal use), then `PATCH Tickets` with `id` and `status`. Companies: `POST Companies/query` with `isActive eq true`, paged through `nextPageUrl`. Picklists from `GET Tickets/entityInformation/fields`.

## 3. Pages

### PSA settings page

- New page `/psa/settings`, policy `AlertsManage`, menu entry "PSA settings" beside "Alert settings".
- One tab per PSA (HaloPSA, ConnectWise, Autotask), each showing whether it is on.
- The HaloPSA section moves off Alert settings unchanged in behaviour. Alert settings keeps the global ticket rules, which apply to all PSAs, and links to the new page.
- Each tab has, with `FieldWithHelp` help on every field:
  - **Connection:** Enabled switch, connection fields, secret (write-only, showing whether one is saved), and the vendor identifier override with help explaining the built-in default.
  - **Ticket defaults:** pick-lists loaded from the PSA, keeping saved names so they show before the lists load. ConnectWise: Board, then Status and Type for that board, Priority, Closed status. Autotask: Queue, Ticket type, Issue type, Priority, Closed status.
  - **Test integration** (section 3.5).
  - **Clear cached sign-in** for HaloPSA, **Clear cached zone** for Autotask.

### Groups page

- One company column per ready PSA ("Halo client", "ConnectWise company", "Autotask company"), each a `SearchableSelect` with loose-name suggestions. `HaloGroupSuggestions` becomes `PsaCompanySuggestions` and works on `PsaCompany`. Halo's "Unknown" client (ID 1) stays excluded for Halo only.
- "Companies without a group" gets a PSA picker. Creating a group links it to that company and suggests links in the other ready PSAs where a company name matches.
- If one PSA's companies fail to load, a warning names that PSA and its column shows saved names read-only. Other columns keep working.
- `GroupManagementService.SetPsaCompanyAsync(context, actor, groupId, psa, PsaCompany? company, ct)` replaces `SetHaloClientIdAsync`.

### Domains page

The domain edit dialog has a company override per ready PSA, with "Use Group's" as the empty value. `DomainManagementService.SetPsaCompanyAsync(context, actor, domainId, psa, PsaCompany? company, ct)` replaces `SetHaloClientIdAsync`.

### Import

- New columns `connectwise company` (aliases `connectwise`, `connectwisecompany`) and `autotask company` (aliases `autotask`, `autotaskcompany`), alongside `halo client`.
- Each is matched by name against that PSA's company list, with the same unknown-name choices as today.
- A notice when the PSA isn't connected: "ConnectWise isn't connected, so the ConnectWise company column was ignored."
- `ImportNameKind` gains `ConnectWiseCompany` and `AutotaskCompany`. `ImportSnapshot` holds a company list and an unavailable reason per PSA. `DomainTarget` holds a set-company entry per PSA. The sample file gains the two columns.

### Alerts page

The ticket column shows a chip per ticket: PSA name and ticket number, linking to the ticket where the PSA allows a stable URL (Halo `/ticket?id=`, ConnectWise `ConnectWise.aspx?routeTo=ServiceFV&recid=`, Autotask `ExecuteCommand.aspx?Code=OpenTicketDetail&TicketID=`). Acknowledging reports the counts, for example "Closed 2 tickets. 1 needs closing by hand in Autotask."

### 3.5 Integration test

`HaloIntegrationTestService` becomes `PsaIntegrationTestService.RunAsync(IPsaProvider provider, string? chosenCompanyId, progress, ct)`, with the same progress UI. Steps:

1. Check settings.
2. Pick an alert and company (the most recent alert's company, or a sample alert against a chosen company).
3. Create a ticket.
4. Read its state (expects Open).
5. Close it.
6. Read its state again (expects Closed).

HaloPSA adds "Wait for Halo's webhook" after step 6, as today. Each run is audited with the PSA's `integration_tested` action.

### Demo mode

A fake client per PSA with a handful of sample companies, whose tickets read as Closed a few minutes after creation, so the demo shows all three tabs, company columns and the poller resolving an alert.

## 4. Errors

- Each provider turns HTTP failures into short messages naming the PSA and the likely fix, for example "ConnectWise refused the sign-in: check the company ID, public key and private key." Response bodies are trimmed before being shown or logged.
- `LogRedactor` redacts the `clientId`, `ApiIntegrationCode`, `UserName` and `Secret` headers and ConnectWise Basic auth.
- Autotask: a failed zone lookup reports "Autotask couldn't find a zone for that username." A 401 clears the cached zone so the next call looks it up again.
- Migrations are hand-written, as with the alert channel switches: create tables, copy data, drop old columns. `Down` reverses the copy for HaloPSA only (ConnectWise and Autotask rows are dropped).

## 5. Testing

- **Unit:** `PsaCompanyResolver` per PSA, including a domain with different deciding Groups per PSA; `AlertTicketPolicy` per PSA; dedup per PSA; one PSA failing during create and close doesn't stop the others; the poller table above row by row, plus backoff and reset.
- **Provider clients:** stub `HttpMessageHandler` tests in the style of the Halo client tests. ConnectWise: auth header, `clientId` header, create and close payloads, closed and missing states, company paging. Autotask: headers, zone lookup and caching, zone cleared on 401, create and close payloads, states, company paging, picklist parsing.
- **Migrations:** in the `AlertChannelMigrationTests` style. Seed Halo client IDs on Groups and Domains and alerts with tickets (open and resolved), migrate, check `PsaCompanyLink` and `AlertTicket`.
- **Existing Halo tests** (webhook, integration test, ticket service, import, groups) move to the new shapes and keep asserting the same behaviour.
- **Audit coverage:** every new mutating service method takes the actor right after the context.
- **Browser check** in demo mode with playwright-edge: PSA settings tabs, Groups page company columns, an alert with tickets in two PSAs.

## 6. Delivery order

One plan in three phases. Each leaves the suite green and could ship alone.

1. **Groundwork plus HaloPSA:** `PsaKind`, `PsaCompanyLink`, `AlertTicket` and their migrations; `IPsaProvider` with the Halo provider; the multi-PSA ticket service; `PsaTicketClosure`; the poller; the PSA settings page with the HaloPSA tab; generalised Groups, Domains, import, Alerts pages and integration test; API acknowledgement counts. HaloPSA behaves as before.
2. **ConnectWise:** settings, migration, client, provider, tab, import column, demo fake, docs page.
3. **Autotask:** the same for Autotask.

Docs: `psa-integration.mdx` becomes an overview with a page each for HaloPSA, ConnectWise and Autotask, covering credentials and the minimum permissions (ConnectWise security role, Autotask API user security level). `website/scripts/canny-roadmap.json` marks both PSAs complete, and marks the Public API complete for v0.8.0 and Slack complete for v0.9.0.
