---
sidebar_position: 3
description: Deploy dotMARC to Azure Container Apps using the included Bicep template.
---

# Deploy to Azure

`infra/main.bicep` provisions everything needed to run dotMARC on Azure:

* A **Container Apps environment** (backed by a Log Analytics workspace) and a **Container App**
  running the published dotMARC image, with a system-assigned managed identity, sticky sessions
  (required for Blazor Server), and a single replica.
* An **Azure Database for PostgreSQL Flexible Server** (`Standard_B1ms`, PostgreSQL 18) with a
  `dotmarc` database and a firewall rule allowing Azure services.
* A **Key Vault** (RBAC-authorized), with the Container App's managed identity granted the
  `Key Vault Secrets User` role.

Before deploying, complete the two Entra app registrations described in
[Getting Started](./getting-started.md) — the Bicep template takes the same non-secret client
IDs/tenant IDs as deployment parameters, and the client secrets are set into Key Vault after
deployment (see below).

## 1. Fill in the parameters

Copy `infra/main.parameters.json` and replace the `REPLACE_ME` placeholders — as checked in, the
file contains placeholder values only and is **not meant to be deployed as-is**. At minimum, set
`postgresAdminPassword`, `graphClientId`, `graphTenantId`, `graphMailboxAddress`,
`entraIdTenantId`, and `entraIdClientId`. For `containerImage`, use the GHCR image published by
CI/CD — `ghcr.io/homotechsual/dotmarc:latest`, or a specific version tag from a release — rather
than building your own.

Alternatively, leave the file untouched and pass overrides inline with `--parameters key=value` on
the command below.

## 2. Deploy

```powershell
$RG = 'your-dotmarc-resourcegroup'
az group create --name $RG --location uksouth
az deployment group create `
  --resource-group $RG `
  --template-file infra/main.bicep `
  --parameters infra/main.parameters.json
```

## 3. Register the deployed hostname as an OIDC redirect URI

The Container App's hostname isn't known until after deployment (Container Apps assigns its FQDN
from the environment's own DNS suffix), so the redirect URI registered in the dashboard sign-in app
registration during one-time setup can't be filled in ahead of time. Read the deployed URL from the
template's `containerAppUrl` output:

```powershell
az deployment group show --resource-group $RG --name main --query properties.outputs.containerAppUrl.value -o tsv
```

Then, in the `dotmarc-dashboard` app registration's **Authentication** blade, add a **Web**
platform redirect URI of `<that URL>/signin-oidc` (alongside or replacing the placeholder one
added during one-time setup). Sign-in will fail with AADSTS50011 until this is done.

## 4. Populate the Key Vault secrets

The template deliberately provisions three Key Vault secrets — `Graph-ClientSecret`,
`EntraId-ClientSecret`, and `ConnectionStrings-DotMarc` — empty, rather than accepting secret
material as deployment parameters (which would put it on the command line or in a parameters
file). Until these are set, the app can't sign in or reach Postgres. Populate them directly:

```powershell
$RG = 'dotmarc-rg'
$KV = az deployment group show --resource-group $RG --name main --query properties.outputs.keyVaultName.value -o tsv
$PG_FQDN = az deployment group show --resource-group $RG --name main --query properties.outputs.postgresServerFqdn.value -o tsv

az keyvault secret set --vault-name $KV --name Graph-ClientSecret --value "<graph app client secret>"
az keyvault secret set --vault-name $KV --name EntraId-ClientSecret --value "<entra id app client secret>"
az keyvault secret set --vault-name $KV --name ConnectionStrings-DotMarc `
  --value "Host=$PG_FQDN;Database=dotmarc;Username=<postgresAdminUsername>;Password=<postgresAdminPassword>;Ssl Mode=Require"

$APP = az deployment group show --resource-group $RG --name main --query properties.outputs.containerAppName.value -o tsv
$REVISION = az containerapp show --resource-group $RG --name $APP --query properties.latestRevisionName -o tsv
az containerapp revision restart --resource-group $RG --name $APP --revision $REVISION
```

Substitute the two Entra app registration client secrets created in the one-time setup steps
above, and the `postgresAdminUsername`/`postgresAdminPassword` values used in step 1. The
container app's secrets reference these by versionless Key Vault URL, so
`az containerapp revision restart` forces it to re-fetch them immediately rather than waiting for
their normal refresh cycle.

:::danger[Don't forget InitialAdmins\_\_Emails]
Set `InitialAdmins__Emails` (see [Getting Started](./getting-started.md#configure)) before this
deployment's first startup — without it, the tightened authorization policy locks out every user,
including you.
:::
