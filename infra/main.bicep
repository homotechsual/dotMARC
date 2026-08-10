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
var appServicePlanName = '${baseName}-plan'
var webAppName = '${baseName}-${uniqueString(resourceGroup().id)}'
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

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: webAppName
  location: location
  kind: 'app,linux,container'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOCKER|${containerImage}'
      alwaysOn: true
      webSocketsEnabled: true
      appSettings: [
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'false' }
        { name: 'WEBSITES_PORT', value: '8080' }
        { name: 'Graph__ClientId', value: graphClientId }
        { name: 'Graph__TenantId', value: graphTenantId }
        { name: 'Graph__MailboxAddress', value: graphMailboxAddress }
        { name: 'EntraId__TenantId', value: entraIdTenantId }
        { name: 'EntraId__ClientId', value: entraIdClientId }
        { name: 'Graph__ClientSecret', value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=Graph-ClientSecret)' }
        { name: 'EntraId__ClientSecret', value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=EntraId-ClientSecret)' }
        { name: 'ConnectionStrings__DotMarc', value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=ConnectionStrings-DotMarc)' }
      ]
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
  name: guid(keyVault.id, webApp.id, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// These three secrets are provisioned empty. The app cannot function until they're set — see the
// README's "Deploy to Azure" section for the az keyvault secret set commands run after deployment.
// Web App settings above reference them by name (not by version), so setting a new value takes
// effect without redeploying the template.
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

output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppName string = webApp.name
output postgresServerFqdn string = postgresServer.properties.fullyQualifiedDomainName
output keyVaultName string = keyVault.name
