# dotMARC

A self-hosted DMARC aggregate report analyzer for monitoring email authentication posture across
multiple domains from a single mailbox. See `docs/superpowers/specs/2026-08-09-dotmarc-design.md`
for the full design.

## One-time setup: two Entra app registrations

dotMARC needs **two separate** Entra app registrations — do not reuse one for both purposes:

### 1. Mailbox access (app-only)

1. **App registrations** → **New registration**, name it e.g. `dotmarc-mailbox`.
2. **API permissions** → add Microsoft Graph **Application** permission `Mail.Read`, then grant
   admin consent.
3. **Certificates & secrets** → create a client secret.
4. Restrict this app's mailbox access via an Exchange **Application Access Policy**, scoped to
   just the DMARC reports mailbox — `Mail.Read` is tenant-wide by default otherwise:

   ```powershell
   New-ApplicationAccessPolicy -AppId <client-id> -PolicyScopeGroupId <mailbox-address> -AccessRight RestrictAccess -Description "dotMARC: restrict to DMARC reports mailbox only"
   ```

### 2. Dashboard sign-in (delegated)

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
| `ConnectionStrings__DotMarc` | PostgreSQL connection string; defaults to host=localhost;database=dotmarc;username=dotmarc;password=dotmarc |

## Run

```bash
GRAPH_CLIENT_ID=... GRAPH_TENANT_ID=... GRAPH_CLIENT_SECRET=... GRAPH_MAILBOX_ADDRESS=... \
ENTRAID_TENANT_ID=... ENTRAID_CLIENT_ID=... ENTRAID_CLIENT_SECRET=... \
docker compose up
```

This runs dotMARC and a PostgreSQL 18 database together, with Postgres data persisted in a named
Docker volume (`dotmarc-postgres-data`). Set the six required environment variables from the setup
steps above (or put them in a `.env` file next to `docker-compose.yml` — compose reads that
automatically).

### Reverse proxy / TLS termination

The container listens on plain HTTP on port 8080; it expects a TLS-terminating reverse proxy
(nginx, Traefik, an Azure/AWS load balancer, etc.) in front of it, forwarding `X-Forwarded-For` and
`X-Forwarded-Proto`. `Program.cs` is configured to honor those headers so the OIDC sign-in redirect
is built as `https://...` instead of `http://...` (without this, sign-in fails with AADSTS50011
because the redirect URI sent to Entra doesn't match the `https://` one registered on the
dashboard app registration).

`ForwardedHeadersOptions`'s default `KnownProxies`/`KnownNetworks` only trust a proxy running on
loopback (i.e. the proxy and dotMARC on the same host). If your reverse proxy runs on a different
host or in a different container, add its address to `KnownProxies` (or its subnet to
`KnownNetworks`) in that configuration block — otherwise ASP.NET Core ignores the forwarded headers
as untrusted and the redirect URI problem above will resurface.

## Deploy to Azure

`infra/main.bicep` provisions everything needed to run dotMARC on Azure:

- An **App Service Plan** (Linux, `B1`) and a **Linux Web App for Containers** running the
  published dotMARC image, with a system-assigned managed identity.
- An **Azure Database for PostgreSQL Flexible Server** (`Standard_B1ms`, PostgreSQL 18) with a
  `dotmarc` database and a firewall rule allowing Azure services.
- A **Key Vault** (RBAC-authorized), with the Web App's managed identity granted the
  `Key Vault Secrets User` role.

Before deploying, complete the **two Entra app registrations** described in
[One-time setup: two Entra app registrations](#one-time-setup-two-entra-app-registrations) above —
the Bicep template takes the same non-secret client IDs/tenant IDs as deployment parameters, and
the client secrets are set into Key Vault after deployment (see below).

### 1. Fill in the parameters

Copy `infra/main.parameters.json` and replace the `REPLACE_ME` placeholders — as checked in, the
file contains placeholder values only and is **not meant to be deployed as-is**. At minimum, set
`postgresAdminPassword`, `graphClientId`, `graphTenantId`, `graphMailboxAddress`,
`entraIdTenantId`, and `entraIdClientId`. For `containerImage`, use the GHCR image published by
CI/CD — `ghcr.io/homotechsual/dotmarc:latest`, or a specific version tag from a release — rather
than building your own.

Alternatively, leave the file untouched and pass overrides inline with `--parameters key=value` on
the command below.

### 2. Deploy

```bash
az group create --name dotmarc-rg --location uksouth
az deployment group create \
  --resource-group dotmarc-rg \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json
```

### 3. Populate the Key Vault secrets

The template deliberately provisions three Key Vault secrets — `Graph-ClientSecret`,
`EntraId-ClientSecret`, and `ConnectionStrings-DotMarc` — empty, rather than accepting secret
material as deployment parameters (which would put it on the command line or in a parameters
file). Until these are set, the app can't sign in or reach Postgres. Populate them directly:

```bash
RG=dotmarc-rg
KV=$(az deployment group show --resource-group $RG --name main --query properties.outputs.keyVaultName.value -o tsv)
PG_FQDN=$(az deployment group show --resource-group $RG --name main --query properties.outputs.postgresServerFqdn.value -o tsv)

az keyvault secret set --vault-name $KV --name Graph-ClientSecret --value "<graph app client secret>"
az keyvault secret set --vault-name $KV --name EntraId-ClientSecret --value "<entra id app client secret>"
az keyvault secret set --vault-name $KV --name ConnectionStrings-DotMarc \
  --value "Host=$PG_FQDN;Database=dotmarc;Username=<postgresAdminUsername>;Password=<postgresAdminPassword>;Ssl Mode=Require"

az webapp restart --resource-group $RG --name $(az deployment group show --resource-group $RG --name main --query properties.outputs.webAppName.value -o tsv)
```

Substitute the two Entra app registration client secrets created in the one-time setup steps
above, and the `postgresAdminUsername`/`postgresAdminPassword` values used in step 1. The Web
App's `appSettings` reference these secrets by name (not by version), so `az webapp restart`
forces it to re-fetch the Key Vault references immediately rather than waiting for their normal
refresh cycle.

## Development

```bash
dotnet build dotMARC.sln
dotnet test dotMARC.sln
```

Point each monitored domain's DMARC record's `rua=` tag at the same mailbox this app polls, e.g.:

```
v=DMARC1; p=quarantine; rua=mailto:dmarc-reports@yourtenant.com
```

## Scope

See the design spec's Non-goals section — forensic (RUF) reports, push notifications (email
digest, real-time alerts), and the 12-month raw-data rollup job are all deliberately out of scope
for this build.
