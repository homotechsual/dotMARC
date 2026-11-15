// Adds only what MTA-STS policy hosting needs to an EXISTING dotMARC deployment on Azure: the two narrow custom roles
// and their assignments to the Container App's own managed identity. It touches nothing else, so it is safe to run
// against an installation that is already working (no PostgreSQL, Key Vault, ingress or revision changes).
//
// The role names are built from the same seeds as the ones in main.bicep, so a later full deployment of main.bicep
// with enableMtaStsHosting=true adopts these roles instead of trying to create duplicates. Keep the two files in step.
//
// Most people run scripts/azure/enable-mta-sts-hosting.ps1, which deploys this module and also sets the environment
// variables. See "Turn on MTA-STS hosting for an existing install" in the Azure deployment guide.

@description('The base name the installation was deployed with. The Container App has this name, and the role names include it. The default is "dotmarc".')
param baseName string = 'dotmarc'

@description('The Container App to grant the roles to. Defaults to the base name, which is what main.bicep uses.')
param containerAppName string = baseName

@description('The Container Apps managed environment the app runs in. Defaults to "<baseName>-env", which is what main.bicep uses.')
param environmentName string = '${baseName}-env'

resource containerAppEnv 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: containerAppName
}

resource mtaStsContainerAppRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(containerApp.id, 'dotMARC MTA-STS Container App Role')
  properties: {
    roleName: 'dotMARC MTA-STS Container App Role (${baseName})'
    description: 'Lets dotMARC bind mta-sts subdomains as custom domains to its own Container App.'
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

resource mtaStsContainerAppRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerApp.id, 'dotMARC MTA-STS Container App Role Assignment')
  scope: containerApp
  properties: {
    roleDefinitionId: mtaStsContainerAppRole.id
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource mtaStsManagedEnvironmentRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
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

resource mtaStsManagedEnvironmentRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerAppEnv.id, 'dotMARC MTA-STS Managed Environment Role Assignment')
  scope: containerAppEnv
  properties: {
    roleDefinitionId: mtaStsManagedEnvironmentRole.id
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output containerAppRoleId string = mtaStsContainerAppRole.id
output managedEnvironmentRoleId string = mtaStsManagedEnvironmentRole.id
