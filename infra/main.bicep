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

@description('Non-secret dashboard sign-in config.')
param entraIdTenantId string
param entraIdClientId string

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
            { name: 'EntraId__TenantId', value: entraIdTenantId }
            { name: 'EntraId__ClientId', value: entraIdClientId }
            { name: 'Graph__ClientSecret', secretRef: 'graph-client-secret' }
            { name: 'EntraId__ClientSecret', secretRef: 'entraid-client-secret' }
            { name: 'ConnectionStrings__DotMarc', secretRef: 'connectionstrings-dotmarc' }
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

output containerAppUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output containerAppName string = containerApp.name
output postgresServerFqdn string = postgresServer.properties.fullyQualifiedDomainName
output keyVaultName string = keyVault.name
