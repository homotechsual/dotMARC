<#
.SYNOPSIS
Turns on MTA-STS policy hosting for an existing dotMARC deployment on Azure Container Apps, without redeploying
anything else.

.DESCRIPTION
For an installation that is already running. It:
  1. Checks the subscription, resource group, Container App and its managed identity.
  2. Deploys infra/mta-sts-hosting.bicep, which creates only the two custom roles MTA-STS hosting needs and assigns
     them to the Container App's managed identity. PostgreSQL, Key Vault and the ingress are not touched.
  3. Sets the MtaSts__* environment variables on the Container App, which creates a new revision.
  4. If Azure can't resolve the Key Vault secret references while creating that revision, re-applies them and tries
     once more (the same fix as step 5c of the Azure deployment guide).
  5. Waits for the new revision to be healthy.

It is safe to run again. Needs the Azure CLI (az), signed in with: az login. Azure Cloud Shell (PowerShell) has both
ready to use. You need Owner (or User Access Administrator plus Contributor) on the resource group.

After it finishes, enable MTA-STS for a domain inside dotMARC and use Push DNS Records.

.PARAMETER ResourceGroup
The resource group dotMARC is deployed in.

.PARAMETER Name
The Container App's name. This is also the base name the installation was deployed with. Defaults to dotmarc.

.PARAMETER EnvironmentName
The Container Apps environment's name. Leave it out to use the one the app is already in.

.PARAMETER HostingHostname
The hostname customers point their mta-sts CNAME records at. Leave it out to use the Container App's own
*.azurecontainerapps.io hostname, which is what Azure's free managed certificates require.

.PARAMETER PreviewOnly
Show what would change and stop, without changing anything.

.PARAMETER TemplateFile
Path to mta-sts-hosting.bicep. Leave it out to use the copy next to this repository's scripts folder, or to download
it from GitHub when you only have this script (as in Cloud Shell).

.EXAMPLE
./enable-mta-sts-hosting.ps1 -ResourceGroup dotmarc-rg -PreviewOnly

.EXAMPLE
./enable-mta-sts-hosting.ps1 -ResourceGroup dotmarc-rg
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,

    [string]$Name = 'dotmarc',

    [string]$EnvironmentName = '',

    [string]$HostingHostname = '',

    [switch]$PreviewOnly,

    [string]$TemplateFile = ''
)

$ErrorActionPreference = 'Stop'

$templateUrl = 'https://raw.githubusercontent.com/homotechsual/dotMARC/main/infra/mta-sts-hosting.bicep'

# Runs the Azure CLI and returns its standard output as one trimmed string. A native command's failure doesn't stop a
# PowerShell script by itself, so check the exit code and surface what az said on its error stream.
#
# This is deliberately not an advanced function: with [CmdletBinding()], PowerShell would try to match az's own
# short options (such as -o) to its common parameters (-OutVariable, -OutBuffer) and refuse to run.
function Invoke-Az {
    $Arguments = [string[]]$args
    $errorFile = [System.IO.Path]::GetTempFileName()
    try {
        $output = & az @Arguments 2>$errorFile
        if ($LASTEXITCODE -ne 0) {
            $message = (Get-Content -Raw -Path $errorFile).Trim()
            throw "az $($Arguments[0..1] -join ' ') failed: $message"
        }
        return (($output | Out-String).Trim())
    }
    finally {
        Remove-Item -Path $errorFile -ErrorAction SilentlyContinue
    }
}

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "== $Message" -ForegroundColor Cyan
}

# ---- 1. Preflight ----------------------------------------------------------------------------------------------

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "The Azure CLI (az) isn't installed. Azure Cloud Shell has it ready to use."
}

Write-Step 'Checking the subscription and the existing installation'

try {
    $account = Invoke-Az account show --query '{name:name,id:id}' -o json | ConvertFrom-Json
}
catch {
    throw 'Sign in first with: az login'
}
Write-Host "Subscription:   $($account.name) ($($account.id))"
Write-Host "Resource group: $ResourceGroup"

try {
    $app = Invoke-Az containerapp show --resource-group $ResourceGroup --name $Name -o json | ConvertFrom-Json
}
catch {
    throw "Couldn't find a Container App named '$Name' in resource group '$ResourceGroup' in this subscription. Check the subscription above (az account set --subscription <id>), the resource group, and -Name. Details: $($_.Exception.Message)"
}

$principalId = $app.identity.principalId
if (-not $principalId) {
    throw "The Container App '$Name' has no system-assigned managed identity, which dotMARC's Azure deployment always has. Is this the right app?"
}

$environmentId = $app.properties.managedEnvironmentId
if (-not $EnvironmentName) {
    $EnvironmentName = ($environmentId -split '/')[-1]
}

$fqdn = $app.properties.configuration.ingress.fqdn
if (-not $fqdn) {
    throw "The Container App '$Name' has no ingress hostname. dotMARC's Azure deployment always enables external ingress."
}
if (-not $HostingHostname) {
    $HostingHostname = $fqdn
}

$secretsWithVault = @($app.properties.configuration.secrets | Where-Object { $_.keyVaultUrl })
$existingEnv = @($app.properties.template.containers[0].env)
$currentHostname = ($existingEnv | Where-Object { $_.name -eq 'MtaSts__HostingHostname' }).value

Write-Host "Container App:  $Name (managed identity $principalId)"
Write-Host "Environment:    $EnvironmentName"
Write-Host "App hostname:   $fqdn"
Write-Host "Hosting host:   $HostingHostname"
Write-Host "Key Vault refs: $($secretsWithVault.Count) secret reference(s)"
if ($currentHostname) {
    Write-Host "MTA-STS hosting is already configured (MtaSts__HostingHostname = $currentHostname). Running again is safe." -ForegroundColor Yellow
}
if ($HostingHostname -ne $fqdn) {
    Write-Host "Warning: Azure's free managed certificates need customers' CNAMEs to point at the app's own hostname ($fqdn), not another custom domain." -ForegroundColor Yellow
}

$revisionHealth = Invoke-Az containerapp revision list --resource-group $ResourceGroup --name $Name -o json | ConvertFrom-Json
$activeRevisions = @($revisionHealth | Where-Object { $_.properties.active })
foreach ($revision in $activeRevisions) {
    Write-Host "Active revision: $($revision.name) is $($revision.properties.healthState) / $($revision.properties.runningState)"
}
if ($activeRevisions | Where-Object { $_.properties.healthState -ne 'Healthy' }) {
    Write-Host 'Warning: an active revision is not healthy before we start. Fix that first if you can.' -ForegroundColor Yellow
}

# ---- 2. The Bicep module ---------------------------------------------------------------------------------------

if (-not $TemplateFile) {
    $local = Join-Path $PSScriptRoot '../../infra/mta-sts-hosting.bicep'
    if (Test-Path $local) {
        $TemplateFile = (Resolve-Path $local).Path
    }
    else {
        $TemplateFile = Join-Path ([System.IO.Path]::GetTempPath()) 'dotmarc-mta-sts-hosting.bicep'
        Write-Host "Downloading $templateUrl"
        Invoke-WebRequest -Uri $templateUrl -OutFile $TemplateFile
    }
}
if (-not (Test-Path $TemplateFile)) {
    throw "Template file not found: $TemplateFile"
}

$parameterArguments = @("baseName=$Name", "containerAppName=$Name", "environmentName=$EnvironmentName")

if ($PreviewOnly) {
    Write-Step 'Preview: what the roles deployment would change'
    & az deployment group what-if --resource-group $ResourceGroup --template-file $TemplateFile --parameters @parameterArguments
    Write-Host ''
    Write-Host 'It would then set these environment variables on the Container App (creating a new revision):'
    Write-Host "  MtaSts__HostingHostname=$HostingHostname"
    Write-Host '  MtaSts__Provisioner=Azure'
    Write-Host "  MtaSts__AzureSubscriptionId=$($account.id)"
    Write-Host "  MtaSts__AzureResourceGroupName=$ResourceGroup"
    Write-Host "  MtaSts__AzureContainerAppName=$Name"
    Write-Host "  MtaSts__AzureManagedEnvironmentName=$EnvironmentName"
    Write-Host ''
    Write-Host 'Preview only: nothing was changed.' -ForegroundColor Green
    return
}

Write-Step 'Creating the two MTA-STS roles and assigning them to the app'
Invoke-Az deployment group create --resource-group $ResourceGroup --name 'dotmarc-mta-sts-hosting' --template-file $TemplateFile --parameters @parameterArguments --query properties.provisioningState -o tsv | Out-Null
Write-Host 'Roles in place.'

# ---- 3. Environment variables ----------------------------------------------------------------------------------

$environmentVariables = @(
    "MtaSts__HostingHostname=$HostingHostname",
    'MtaSts__Provisioner=Azure',
    "MtaSts__AzureSubscriptionId=$($account.id)",
    "MtaSts__AzureResourceGroupName=$ResourceGroup",
    "MtaSts__AzureContainerAppName=$Name",
    "MtaSts__AzureManagedEnvironmentName=$EnvironmentName"
)

function Set-MtaStsEnvironment {
    Invoke-Az containerapp update --resource-group $ResourceGroup --name $Name --set-env-vars @environmentVariables --query properties.latestRevisionName -o tsv
}

# Azure resolves the Key Vault secret references when it creates a revision, and sometimes can't ("Unable to get value
# using Managed identity System for secret ..."). Re-applying all of the references together, then trying again, is the
# fix from step 5c of the deployment guide.
function Restore-KeyVaultReferences {
    if ($secretsWithVault.Count -eq 0) {
        throw "Azure couldn't resolve the app's secrets, and the app has no Key Vault references to re-apply. Check the Key Vault and the app's role on it."
    }
    $secretArguments = $secretsWithVault | ForEach-Object { "$($_.name)=keyvaultref:$($_.keyVaultUrl),identityref:system" }
    Write-Host "Re-applying $($secretsWithVault.Count) Key Vault reference(s): $((($secretsWithVault | ForEach-Object { $_.name }) -join ', '))"
    Invoke-Az containerapp secret set --resource-group $ResourceGroup --name $Name --secrets @secretArguments | Out-Null
}

Write-Step 'Setting the MTA-STS environment variables (this creates a new revision)'
try {
    $newRevision = Set-MtaStsEnvironment
}
catch {
    if ($_.Exception.Message -notmatch 'Managed identity|secret') {
        throw
    }
    Write-Host "Azure couldn't resolve a Key Vault secret reference while creating the revision:" -ForegroundColor Yellow
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Yellow
    Restore-KeyVaultReferences
    Write-Host 'Trying the environment variables again.'
    $newRevision = Set-MtaStsEnvironment
}

# ---- 4. Wait for a healthy revision ----------------------------------------------------------------------------

Write-Step "Waiting for revision $newRevision to be healthy"
$deadline = (Get-Date).AddMinutes(6)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    $revisions = Invoke-Az containerapp revision list --resource-group $ResourceGroup --name $Name -o json | ConvertFrom-Json
    $current = $revisions | Where-Object { $_.name -eq $newRevision }
    if ($current) {
        Write-Host "  $($current.name): $($current.properties.healthState) / $($current.properties.runningState)"
        if ($current.properties.healthState -eq 'Healthy' -and $current.properties.runningState -match 'Running') {
            $healthy = $true
            break
        }
    }
    Start-Sleep -Seconds 15
}

if (-not $healthy) {
    Write-Host ''
    Write-Host "Revision $newRevision isn't healthy yet." -ForegroundColor Yellow
    Write-Host 'If it is stuck Activating, re-apply the Key Vault references and restart it (step 5c of the Azure deployment guide), then check again with:'
    Write-Host "  az containerapp revision list --resource-group $ResourceGroup --name $Name --output table"
    exit 1
}

# ---- 5. Done ---------------------------------------------------------------------------------------------------

Write-Step 'Done'
Write-Host "Azure is ready to host MTA-STS policies for this dotMARC. Customers' mta-sts CNAME records should point at:"
Write-Host "  $HostingHostname" -ForegroundColor Green
Write-Host ''
Write-Host 'Next, in dotMARC: open a domain, go to its MTA-STS tab, enable hosting, and use Push DNS Records.'
Write-Host 'Azure then binds the custom domain and issues a certificate, which can take a few minutes.'
