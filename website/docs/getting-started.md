---
sidebar_position: 1
---

# Getting Started

dotMARC needs **two separate** Entra app registrations — do not reuse one for both purposes.

## 1. Mailbox access (app-only)

1. **App registrations** → **New registration**, name it e.g. `dotmarc-mailbox`.
2. **API permissions** → add Microsoft Graph **Application** permission `Mail.Read`, then grant
   admin consent.
3. **Certificates & secrets** → create a client secret.
4. Restrict this app's mailbox access via an Exchange **Application Access Policy**. Exchange
   requires the policy scope to be a **security principal** (for example a mail-enabled security
   group), not the mailbox itself. Create a dedicated group for the DMARC reports mailbox, add
   that mailbox to the group, and then scope the policy to the group:

   ```powershell
   Connect-ExchangeOnline -Organization <your-tenant>

   $group = New-DistributionGroup -Name "dotMARC DMARC Reports Scope" -Type Security -Alias dotmarc-dmarc-reports
   Add-DistributionGroupMember -Identity $group.Identity -Member "dmarc-reports@contoso.com"

   New-ApplicationAccessPolicy -AppId <client-id> -PolicyScopeGroupId $group.ObjectId -AccessRight RestrictAccess -Description "dotMARC: restrict to DMARC reports mailbox only"
   ```

   If you already have the mail-enabled security group, use its `ObjectId` instead of the mailbox
   address. Do not pass the mailbox address directly to `-PolicyScopeGroupId`.

## 2. Dashboard sign-in (delegated)

1. **App registrations** → **New registration**, name it e.g. `dotmarc-dashboard`.
2. **Authentication** → add a **Web** platform redirect URI:
   `https://<your-deployment-host>/signin-oidc`.
3. **Certificates & secrets** → create a client secret. Microsoft.Identity.Web wires this app up
   via the standard confidential-client authorization-code flow, so the token exchange after
   sign-in needs this secret even though the app itself is only used for interactive sign-in.
4. No API permissions needed beyond the default `User.Read`.

## Configure

Set via environment variables (double-underscore nesting):

| Variable | Description |
| --- | --- |
| `Graph__ClientId` | Mailbox app registration's client ID |
| `Graph__TenantId` | Your tenant ID |
| `Graph__ClientSecret` | Mailbox app registration's client secret |
| `Graph__MailboxAddress` | The shared mailbox address receiving DMARC reports |
| `Graph__PollIntervalSeconds` | Default `300` |
| `EntraId__TenantId` | Your tenant ID |
| `EntraId__ClientId` | Dashboard app registration's client ID |
| `EntraId__ClientSecret` | Dashboard app registration's client secret |
| `ConnectionStrings__DotMarc` | PostgreSQL connection string; defaults to `Host=localhost;Database=dotmarc;Username=dotmarc;Password=dotmarc` |
| `InitialAdmins__Emails` | Comma-separated list of email addresses granted the Admin role the very first time the app starts with no existing access grants — either a genuinely fresh install, or this app's first deploy of the permissions feature to an existing live environment. Only takes effect while the `UserAccess` table is empty; harmless to leave set afterwards. |

:::danger Set `InitialAdmins__Emails` before deploying this feature
Authorization is deny-by-default: with no existing access grants, the fallback policy locks out
every user, including the operator, unless `InitialAdmins__Emails` seeds at least one Admin grant
on that first startup. If you deploy without it, recovery requires direct database access to
insert a `UserAccess` row pointing at a locked `Admin` role.
:::

## Run

```powershell
$env:GRAPH_CLIENT_ID = '...'
$env:GRAPH_TENANT_ID = '...'
$env:GRAPH_CLIENT_SECRET = '...'
$env:GRAPH_MAILBOX_ADDRESS = '...'
$env:ENTRAID_TENANT_ID = '...'
$env:ENTRAID_CLIENT_ID = '...'
$env:ENTRAID_CLIENT_SECRET = '...'
docker compose up
```

This runs dotMARC and a PostgreSQL 18 database together, with Postgres data persisted in a named
Docker volume (`dotmarc-postgres-data`). Set the required environment variables from the setup
steps above (or put them in a `.env` file next to `docker-compose.yml` — compose reads that
automatically).

### Reverse proxy / TLS termination

The container listens on plain HTTP on port 8080; it expects a TLS-terminating reverse proxy
(nginx, Traefik, an Azure/AWS load balancer, etc.) in front of it, forwarding `X-Forwarded-For` and
`X-Forwarded-Proto`. Without this, sign-in fails with AADSTS50011 because the redirect URI sent to
Entra doesn't match the `https://` one registered on the dashboard app registration.

Point each monitored domain's DMARC record's `rua=` tag at the same mailbox this app polls, e.g.:

```txt
v=DMARC1; p=quarantine; rua=mailto:dmarc-reports@yourtenant.com
```

Next: [Local Development](./local-development.md) to run and test the app from source, or
[Deploy to Azure](./deploy-to-azure.md) to run it in production.
