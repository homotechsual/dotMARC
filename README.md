# dotMARC

A self-hosted DMARC aggregate report analyzer for monitoring email authentication posture across
multiple domains from a single mailbox. See `docs/superpowers/specs/2026-08-09-dotmarc-design.md`
for the full design.

## One-time setup: two Entra app registrations

dotMARC needs **two separate** Entra app registrations: do not reuse one for both purposes:

To create both registrations and their client secrets with Azure CLI, sign in to the intended tenant
and run [`scripts/entra/create-dotmarc-apps.sh`](scripts/entra/create-dotmarc-apps.sh) in Bash. Pass
`--dashboard-redirect-uri https://<your-host>/signin-oidc` when the host is known, or add the redirect
URI after deploying to Azure Container Apps. Add `--include-mail-send` if you plan to send client
reports through Microsoft Graph. The script adds the Graph permissions but does not grant admin
consent to the mailbox app; restrict mailbox access using the Exchange policy below first. Copy the
secrets it prints, as they cannot be retrieved later.

### 1. Mailbox access (app-only)

1. **App registrations** → **New registration**, name it e.g. `dotmarc-mailbox`.
2. **API permissions** → add Microsoft Graph **Application** permission `Mail.ReadWrite`.
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

   After creating the policy, grant admin consent for `Mail.ReadWrite` in **API permissions**. If you
   used the script, run the `az ad app permission admin-consent --id <mailbox-client-id>` command it
   prints.

### 2. Dashboard sign-in (delegated)

1. **App registrations** → **New registration**, name it e.g. `dotmarc-dashboard`.
2. **Authentication** → add a **Web** platform redirect URI:
   `https://<your-deployment-host>/signin-oidc`.
   Under **Implicit grant and hybrid flows**, enable **ID tokens**.
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
| `Graph__TlsrptMailboxAddress` | Optional separate mailbox address advertised in managed TLSRPT (`_smtp._tls`) records |
| `Graph__PollIntervalSeconds` | Default `300` |
| `EntraId__TenantId` | Your tenant ID |
| `EntraId__ClientId` | Dashboard app registration's client ID |
| `EntraId__ClientSecret` | Dashboard app registration's client secret |
| `ConnectionStrings__DotMarc` | PostgreSQL connection string; defaults to `Host=localhost;Database=dotmarc;Username=dotmarc;Password=dotmarc` |
| `InitialAdmins__Emails` | Comma-separated list of email addresses granted the Admin role the very first time the app starts with no existing access grants - either a genuinely fresh install, or this app's first deploy of the permissions feature to an existing live environment. Only takes effect while the `UserAccess` table is empty; harmless to leave set afterwards. |

> **Set `InitialAdmins__Emails` before deploying this feature.** Authorization is deny-by-default:
> with no existing access grants, the fallback policy locks out every user, including the
> operator, unless `InitialAdmins__Emails` seeds at least one Admin grant on that first startup.
> If you deploy without it, recovery requires direct database access to insert a `UserAccess` row
> pointing at a locked `Admin` role.

## Run

```powershell
$env:GRAPH_CLIENT_ID = '...'
$env:GRAPH_TENANT_ID = '...'
$env:GRAPH_CLIENT_SECRET = '...'
$env:GRAPH_MAILBOX_ADDRESS = '...'
$env:GRAPH_TLSRPT_MAILBOX_ADDRESS = '...'
$env:ENTRAID_TENANT_ID = '...'
$env:ENTRAID_CLIENT_ID = '...'
$env:ENTRAID_CLIENT_SECRET = '...'
docker compose up
```

This runs dotMARC and a PostgreSQL 18 database together, with Postgres data persisted in a named
Docker volume (`dotmarc-postgres-data`). Set the seven required environment variables from the
setup steps above (or put them in a `.env` file next to `docker-compose.yml` compose reads that
automatically).

For upgrades, including the important extra pull/recreate step when using a mutable `:latest`
image, see [Deploy with Docker: Update or redeploy dotMARC](https://dotmarc.app/docs/deploy-with-docker#update-or-redeploy-dotmarc).

### Cut a release

Release metadata lives in `Directory.Build.props`. Prepare the next version with the release
helper; it updates the project version and creates the matching release-notes post and slug:

```powershell
node scripts/release.mjs prepare 0.7.0
```

Edit the generated post, then create the release commit and annotated tag only after its contents
are ready:

```powershell
node scripts/release.mjs tag 0.7.0
git push origin main v0.7.0
```

The tag command refuses unrelated worktree changes, duplicate slugs, missing release notes, or an
existing tag. The release workflow repeats the metadata check before building images. The app
footer reads the compiled project version and links it to the corresponding post on the website.

Pushing the tag also publishes the release post to Canny as a changelog entry and links the
shipped ideas to it. Mark those ideas complete in Canny before you push the tag; see
[Releasing](https://dotmarc.app/docs/releasing) for what is automated and what is not.

### Reverse proxy / TLS termination

The container listens on plain HTTP on port 8080; it expects a TLS-terminating reverse proxy
(nginx, Traefik, an Azure/AWS load balancer, etc.) in front of it, forwarding `X-Forwarded-For` and
`X-Forwarded-Proto`. `Program.cs` is configured to honor those headers so the OIDC sign-in redirect
is built as `https://...` instead of `http://...` (without this, sign-in fails with AADSTS50011
because the redirect URI sent to Entra doesn't match the `https://` one registered on the
dashboard app registration).

`ForwardedHeadersOptions`'s default `KnownProxies`/`KnownIPNetworks` only trust a proxy running on
loopback (i.e. the proxy and dotMARC on the same host), which wouldn't hold for a reverse proxy on
a different host/container, or for Azure Container Apps' ingress proxy (see
[Deploy to Azure](#deploy-to-azure)). `Program.cs` clears both restrictions unconditionally, since
the container has no other ingress path in either supported deployment model, it's never directly
reachable except through that trusted front-end, so any upstream proxy is trusted without needing
per-deployment configuration.

## Deploy to Azure

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fdotmarc.app%2Fazuredeploy.json)

The button opens Azure's deployment form for a Container App, a PostgreSQL Flexible Server and a Key
Vault. The full walkthrough lives in the docs, in order, with every form field explained next to the
button: **[Deploy to Azure](https://dotmarc.app/docs/deploy-to-azure)**. In short:

1. Create the two Entra app registrations (see [One-time setup](#one-time-setup-two-entra-app-registrations) above).
2. Choose the right subscription and a new resource group, then deploy with the button (or `infra/main.bicep`).
3. Find the deployed resources (the docs give a script that does not depend on the deployment name).
4. Register `<app URL>/signin-oidc` as the dashboard app's redirect URI.
5. Grant yourself **Key Vault Secrets Officer**, write the three Key Vault secrets, re-apply all three Container App secret references with `az containerapp secret set`, and restart the revision.
6. Confirm the revision is Healthy and Running.

This is not an unattended installation: the app will not start until steps 4 and 5 are done. The
button uses [dotmarc.app/azuredeploy.json](https://dotmarc.app/azuredeploy.json), generated from
`infra/main.bicep` during every website build, so there is no separate JSON template to maintain.

For an existing deployment, see [Deploy to Azure: Update or redeploy dotMARC](https://dotmarc.app/docs/deploy-to-azure#7-update-or-redeploy-dotmarc)
for checking the active image, updating immutable release tags, forcing a new revision when using
`:latest`, verifying readiness, and rolling back.

## Demo instance

`Demo__Enabled=true` switches the app into demo mode: real Entra/Graph auth and mailbox polling
are skipped entirely, an anonymous `/demo` page lets a visitor sign in as one of two fixed
personas (Demo Admin, or Demo Viewer scoped to one client group), and a generated dataset for a
fictional MSP is (re)written on every startup and again every night at `Demo__ResetHourUtc`
(default `4`, UTC). See
[docs/superpowers/specs/2026-08-28-demo-instance-design.md](docs/superpowers/specs/2026-08-28-demo-instance-design.md)
for the full design and the narrative the generated data tells.

No `Graph__*`/`EntraId__*`/`InitialAdmins__Emails` variables are needed in this mode - only
`ConnectionStrings__DotMarc` and `Demo__Enabled`.

### Running the demo stack

```powershell
docker compose -f docker-compose.demo.yml --env-file .env.demo up -d
```

with a `.env.demo` file next to `docker-compose.demo.yml` containing:

```
DOTMARC_IMAGE=ghcr.io/homotechsual/dotmarc:demo
POSTGRES_PASSWORD=<pick a password>
```

The `dotmarc-demo` container joins an external Docker network named `proxy` and only `expose`s
port 8080 - it does not publish a host port or run its own reverse proxy. Point your existing
Caddy instance (on that same `proxy` network) at it, e.g.:

```
demo.dotmarc.app {
    reverse_proxy dotmarc-demo:8080
}
```

Deployment to the demo VM is automated by `.github/workflows/demo-deploy.yml` on every push to
`main` - see that workflow for the required repository secrets/variables.

## Development

The repo is designed to work in two common local modes:

1. Run the full stack with Docker Compose.
2. Run the app locally while connecting to a PostgreSQL instance on `localhost:5432`.

### Build and test

```powershell
dotnet restore dotMARC.sln
dotnet build dotMARC.sln
dotnet test dotMARC.sln
```

`dotnet test` uses Testcontainers.PostgreSql, so Docker must be running; the first run pulls the
`postgres:18` image automatically.

### Local database-first flow

To start just the database for local debugging:

```powershell
docker compose up postgres
```

This publishes PostgreSQL on `localhost:5432` using the default development connection values from
`src/DotMarc/appsettings.json`: database `dotmarc`, username `dotmarc`, and password `dotmarc`.

If you want to run the app directly on the host instead of using the container stack, use:

```powershell
dotnet run --project src/DotMarc/DotMarc.csproj
```

The app expects the same PostgreSQL connection settings as the Docker Compose setup.

### Full stack with Docker Compose

```powershell
docker compose up --build
```

This starts dotMARC together with PostgreSQL in one command, using the repo's `docker-compose.yml`
configuration and the same environment variables described in the setup section above.

Point each monitored domain's DMARC record's `rua=` tag at the same mailbox this app polls, e.g.:

```txt
v=DMARC1; p=quarantine; rua=mailto:dmarc-reports@yourtenant.com
```

## Scope

See the design spec's Non-goals section forensic (RUF) reports, push notifications (email
digest, real-time alerts), and the 12-month raw-data rollup job are all deliberately out of scope
for this build.

## License

dotMARC is licensed under the [GNU Affero General Public License v3.0](LICENSE.md) (AGPL-3.0):

```text
dotMARC, a self-hosted DMARC aggregate report analyzer
Copyright (C) 2026 Mikey O'Toole

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
```
