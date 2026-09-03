@description('Base name used to derive resource names (e.g. "dotmarc").')
param baseName string = 'dotmarc'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Container image to deploy, e.g. ghcr.io/homotechsual/dotmarc:latest')
param containerImage string = 'ghcr.io/homotechsual/dotmarc:latest'

@description('PostgreSQL administrator username.')
param postgresAdminUsername string = 'dotmarc'

@secure()
@description('PostgreSQL administrator password.')
param postgresAdminPassword string

@description('Non-secret Graph app-only config.')
param graphClientId string
param graphTenantId string
param graphMailboxAddress string

@description('Optional separate mailbox for receiving SMTP TLS Reporting aggregate reports.')
param graphTlsrptMailboxAddress string = ''

@description('Non-secret dashboard sign-in config.')
param entraIdTenantId string
param entraIdClientId string

@description('Comma-separated list of email addresses granted the Admin role the first time the app starts with no existing access grants. Not secret — just email addresses. Only takes effect on that first startup; safe to leave set afterwards.')
param initialAdminEmails string = ''

// Before re-running this template against a resource group that's already deployed (e.g. to
// flip this flag on later), run `az deployment group what-if` first and read the diff — this
// template's `containerApp.ingress` and `postgresServer` resources declare those objects in
// full, so a redeploy replaces a custom domain bound after the original deployment (Portal or
// `az containerapp hostname add`) or Postgres storage/auth settings changed since, not just
// merges into them. See deploy-to-azure.mdx's "Re-running the template later" section for the
// what-if walkthrough and a by-hand fallback (the exact `az role definition create` /
// `az containerapp update --set-env-vars` calls) that applies just this flag's effects without
// touching anything else.
@description('Enable MTA-STS policy hosting (see getting-started.mdx). Grants the container app a narrowly-scoped custom RBAC role to manage its own custom domains and managed certificates — off by default, since it widens the managed identity beyond Key Vault Secrets User.')
param enableMtaStsHosting bool = false

@description('Hostname customers CNAME mta-sts.<their-domain> to. Only meaningful when enableMtaStsHosting is true. The Container App\'s own generated hostname isn\'t known until after a first deployment (see containerAppUrl output) — leave this blank on that first deployment, then set it on a follow-up update, same two-step pattern as the OIDC redirect URI below.')
param mtaStsHostingHostname string = ''

@description('Non-secret Cloudflare DNS push config (see getting-started.mdx#dns-provider-push-optional). Leave blank to leave this provider\'s push button off; the client secret is set into Key Vault after deployment like the other secrets below.')
param cloudflareDnsClientId string = ''

@description('Non-secret Azure DNS push config (see getting-started.mdx#dns-provider-push-optional). Leave blank to leave this provider\'s push button off; the client secret is set into Key Vault after deployment like the other secrets below.')
param azureDnsTenantId string = ''
param azureDnsClientId string = ''

var postgresServerName = '${baseName}-pg-${uniqueString(resourceGroup().id)}'
var keyVaultName = '${take(baseName, 7)}-kv-${uniqueString(resourceGroup().id)}'
var logAnalyticsName = '${baseName}-logs'
var containerAppEnvName = '${baseName}-env'
var containerAppName = baseName
var postgresDatabaseName = 'dotmarc'

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresServerName
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '18'
    administratorLogin: postgresAdminUsername
    administratorLoginPassword: postgresAdminPassword
    storage: {
      storageSizeGB: 32
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource postgresDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgresServer
  name: postgresDatabaseName
}

resource postgresFirewallAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgresServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerAppEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppEnvName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: containerAppEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        // Blazor Server keeps each user's component state in the memory of whichever replica
        // holds their SignalR circuit — there's no shared backing store a request can fall back
        // to. Sticky sessions keep a client's requests on that same replica; only supported in
        // single revision mode, which is why activeRevisionsMode is pinned to 'Single' above.
        stickySessions: {
          affinity: 'sticky'
        }
      }
      secrets: [
        {
          name: 'graph-client-secret'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/Graph-ClientSecret'
          identity: 'System'
        }
        {
          name: 'entraid-client-secret'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/EntraId-ClientSecret'
          identity: 'System'
        }
        {
          name: 'connectionstrings-dotmarc'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/ConnectionStrings-DotMarc'
          identity: 'System'
        }
        {
          name: 'cloudflaredns-client-secret'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/CloudflareDns-ClientSecret'
          identity: 'System'
        }
        {
          name: 'azuredns-client-secret'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/AzureDns-ClientSecret'
          identity: 'System'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'dotmarc'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'Graph__ClientId', value: graphClientId }
            { name: 'Graph__TenantId', value: graphTenantId }
            { name: 'Graph__MailboxAddress', value: graphMailboxAddress }
            { name: 'Graph__TlsrptMailboxAddress', value: graphTlsrptMailboxAddress }
            { name: 'EntraId__TenantId', value: entraIdTenantId }
            { name: 'EntraId__ClientId', value: entraIdClientId }
            { name: 'InitialAdmins__Emails', value: initialAdminEmails }
            // Harmless to always set: PollingService's MTA-STS cycle no-ops entirely whenever
            // MtaSts__HostingHostname is empty (see MtaStsOptions), which is the default above.
            { name: 'MtaSts__HostingHostname', value: mtaStsHostingHostname }
            { name: 'MtaSts__Provisioner', value: 'Azure' }
            { name: 'MtaSts__AzureSubscriptionId', value: subscription().subscriptionId }
            { name: 'MtaSts__AzureResourceGroupName', value: resourceGroup().name }
            { name: 'MtaSts__AzureContainerAppName', value: containerAppName }
            { name: 'MtaSts__AzureManagedEnvironmentName', value: containerAppEnvName }
            // Harmless to always set, same as MtaSts__HostingHostname above: both DNS push
            // providers are independently optional, and each one's push button simply never
            // renders while its config is blank (see CloudflareDnsOptions/AzureDnsOptions).
            { name: 'CloudflareDns__ClientId', value: cloudflareDnsClientId }
            { name: 'AzureDns__TenantId', value: azureDnsTenantId }
            { name: 'AzureDns__ClientId', value: azureDnsClientId }
            { name: 'Graph__ClientSecret', secretRef: 'graph-client-secret' }
            { name: 'EntraId__ClientSecret', secretRef: 'entraid-client-secret' }
            { name: 'ConnectionStrings__DotMarc', secretRef: 'connectionstrings-dotmarc' }
            { name: 'CloudflareDns__ClientSecret', secretRef: 'cloudflaredns-client-secret' }
            { name: 'AzureDns__ClientSecret', secretRef: 'azuredns-client-secret' }
          ]
        }
      ]
      // PollingService and startup migrations are now guarded by Postgres advisory locks, and
      // ingress has sticky sessions — both prerequisites for running more than one replica. Left
      // at 1/1 here to match current capacity; raise maxReplicas whenever scaling out is actually
      // needed, no further app changes required.
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
  }
}

resource keyVaultSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, containerApp.id, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// MTA-STS hosting needs the app's own managed identity to bind custom domains to itself and
// create managed certificates on its own environment (see AzureMtaStsHostProvisioner). Azure has
// no built-in role scoped that narrowly — the only write action that exists for custom domains
// (containerApps/write) also covers the rest of the container app's config, image/scale/env
// included, and the built-in "Container Apps Contributor" role additionally bundles alert-rule
// management and environment create/join rights this identity has no use for. Two separate
// custom roles, each assigned only at the one resource it actually needs (not
// resource-group-wide), is as narrow as Azure's RBAC surface allows for this.
resource mtaStsContainerAppRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = if (enableMtaStsHosting) {
  name: guid(containerApp.id, 'dotMARC MTA-STS Container App Role')
  properties: {
    roleName: 'dotMARC MTA-STS Container App Role (${baseName})'
    description: 'Lets dotMARC bind mta-sts.<domain> custom domains to its own Container App.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.App/containerApps/read'
          'Microsoft.App/containerApps/write'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [
      resourceGroup().id
    ]
  }
}

resource mtaStsContainerAppRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableMtaStsHosting) {
  name: guid(containerApp.id, 'dotMARC MTA-STS Container App Role Assignment')
  scope: containerApp
  properties: {
    roleDefinitionId: mtaStsContainerAppRole.id
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource mtaStsManagedEnvironmentRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = if (enableMtaStsHosting) {
  name: guid(containerAppEnv.id, 'dotMARC MTA-STS Managed Environment Role')
  properties: {
    roleName: 'dotMARC MTA-STS Managed Environment Role (${baseName})'
    description: 'Lets dotMARC create and read managed certificates on its own Container Apps environment, and join that environment when binding a custom domain, for MTA-STS custom domain hosting.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.App/managedEnvironments/read'
          'Microsoft.App/managedEnvironments/join/action'
          'Microsoft.App/managedEnvironments/managedCertificates/read'
          'Microsoft.App/managedEnvironments/managedCertificates/write'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [
      resourceGroup().id
    ]
  }
}

resource mtaStsManagedEnvironmentRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableMtaStsHosting) {
  name: guid(containerAppEnv.id, 'dotMARC MTA-STS Managed Environment Role Assignment')
  scope: containerAppEnv
  properties: {
    roleDefinitionId: mtaStsManagedEnvironmentRole.id
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// These three secrets are provisioned empty. The app cannot function until they're set — see the
// README's "Deploy to Azure" section for the az keyvault secret set commands run after deployment.
// The container app's secrets above reference them by versionless URL, so setting a new value
// takes effect without redeploying the template.
resource graphClientSecretRef 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'Graph-ClientSecret'
  properties: {
    value: ''
  }
}

resource entraIdClientSecretRef 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'EntraId-ClientSecret'
  properties: {
    value: ''
  }
}

resource connectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'ConnectionStrings-DotMarc'
  properties: {
    value: ''
  }
}

// Also provisioned empty, but optional: DNS provider push works with either, both, or neither
// set — see deploy-to-azure.mdx's "Optional: DNS provider push secrets" section. Left unset,
// CloudflareDns__ClientSecret/AzureDns__ClientSecret resolve to an empty string, and that
// provider's push button simply never renders.
resource cloudflareDnsClientSecretRef 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'CloudflareDns-ClientSecret'
  properties: {
    value: ''
  }
}

resource azureDnsClientSecretRef 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'AzureDns-ClientSecret'
  properties: {
    value: ''
  }
}

output containerAppUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output containerAppName string = containerApp.name
output postgresServerFqdn string = postgresServer.properties.fullyQualifiedDomainName
output keyVaultName string = keyVault.name
