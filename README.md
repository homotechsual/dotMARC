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
| `ConnectionStrings__DotMarc` | Defaults to `/app/data/dotmarc.db` inside the container |

## Run

```bash
docker build -f src/DotMarc/Dockerfile -t dotmarc:local .
docker run -d -p 8080:8080 -v dotmarc-data:/app/data \
  -e Graph__ClientId=... -e Graph__TenantId=... -e Graph__ClientSecret=... -e Graph__MailboxAddress=... \
  -e EntraId__TenantId=... -e EntraId__ClientId=... -e EntraId__ClientSecret=... \
  dotmarc:local
```

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
