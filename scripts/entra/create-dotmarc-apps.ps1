<#
.SYNOPSIS
Creates or reuses dotMARC's mailbox and dashboard Entra app registrations, then prints their client IDs and
newly-created secrets.

.DESCRIPTION
The PowerShell equivalent of create-dotmarc-apps.sh, for Windows, macOS and Linux. Needs the Azure CLI (az), signed
in to the target tenant with: az login. Azure Cloud Shell has both ready to use.

.PARAMETER DashboardRedirectUri
An https:// URI ending in /signin-oidc to add to the dashboard app. Leave it out if you don't know the hostname yet
(Azure Container Apps assigns it during deployment) and add it after deploying.

.PARAMETER IncludeMailSend
Also grants the mailbox app the Mail.Send permission, for sending client reports through Microsoft Graph.

.PARAMETER SecretYears
How many years the new client secrets last. Defaults to 2.

.EXAMPLE
./create-dotmarc-apps.ps1 -DashboardRedirectUri https://dotmarc.example.com/signin-oidc
#>
[CmdletBinding()]
param(
    [ValidatePattern('^https://.+/signin-oidc$')]
    [string]$DashboardRedirectUri = '',

    [switch]$IncludeMailSend,

    [ValidateRange(1, 100)]
    [int]$SecretYears = 2
)

$ErrorActionPreference = 'Stop'

$graphAppId = '00000003-0000-0000-c000-000000000000'
$mailReadWriteId = 'e2a3a72e-5f79-4c64-b1b1-878b674786c9'
$mailSendId = 'b633e1c5-b582-4048-a93e-9f11b44c7e96'
$userReadId = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'

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

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "The Azure CLI (az) isn't installed. Azure Cloud Shell has it ready to use."
}

try {
    $tenantId = Invoke-Az account show --query tenantId -o tsv
}
catch {
    throw 'Sign in to the target tenant first with: az login'
}
if (-not $tenantId) {
    throw 'Azure CLI returned no tenant ID. Sign in to the target tenant with: az login'
}

function Find-AppId {
    param([string]$DisplayName)

    $appIds = @((Invoke-Az ad app list --display-name $DisplayName --query '[].appId' -o tsv) -split '\s+' | Where-Object { $_ })
    if ($appIds.Count -gt 1) {
        throw "More than one app registration is named '$DisplayName'. Rename the duplicates before continuing."
    }
    if ($appIds.Count -eq 1) { return $appIds[0] }
    return ''
}

function New-OrExistingApp {
    param([string]$DisplayName)

    $appId = Find-AppId -DisplayName $DisplayName
    if ($appId) {
        Write-Host "Reusing the app registration '$DisplayName' ($appId)."
    }
    else {
        $appId = Invoke-Az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg --query appId -o tsv
        Write-Host "Created the app registration '$DisplayName' ($appId)."
    }
    if (-not $appId) {
        throw "Azure CLI returned no client ID for '$DisplayName'."
    }
    return $appId
}

function Add-GraphPermission {
    param([string]$AppId, [string]$PermissionId, [string]$PermissionType)

    $query = "length(requiredResourceAccess[?resourceAppId=='$graphAppId'].resourceAccess[?id=='$PermissionId'])"
    $existingCount = Invoke-Az ad app show --id $AppId --query $query -o tsv
    if ($existingCount -eq '0') {
        Invoke-Az ad app permission add --id $AppId --api $graphAppId --api-permissions "$PermissionId=$PermissionType" --output none | Out-Null
    }
}

function Add-DashboardRedirectUri {
    param([string]$AppId)

    if (-not $DashboardRedirectUri) { return }

    $existingUris = @((Invoke-Az ad app show --id $AppId --query 'web.redirectUris[]' -o tsv) -split '\s+' | Where-Object { $_ })
    if ($existingUris -notcontains $DashboardRedirectUri) {
        $allUris = @($existingUris) + $DashboardRedirectUri
        Invoke-Az ad app update --id $AppId --web-redirect-uris @allUris --output none | Out-Null
        Write-Host "Added the dashboard redirect URI $DashboardRedirectUri."
    }
}

$mailboxAppId = New-OrExistingApp -DisplayName 'dotmarc-mailbox'
$dashboardAppId = New-OrExistingApp -DisplayName 'dotmarc-dashboard'

Invoke-Az ad app update --id $mailboxAppId --sign-in-audience AzureADMyOrg --output none | Out-Null
Add-GraphPermission -AppId $mailboxAppId -PermissionId $mailReadWriteId -PermissionType Role
if ($IncludeMailSend) {
    Add-GraphPermission -AppId $mailboxAppId -PermissionId $mailSendId -PermissionType Role
}
Invoke-Az ad app update --id $dashboardAppId --sign-in-audience AzureADMyOrg --enable-id-token-issuance true --output none | Out-Null
Add-DashboardRedirectUri -AppId $dashboardAppId
Add-GraphPermission -AppId $dashboardAppId -PermissionId $userReadId -PermissionType Scope

$mailboxSecret = Invoke-Az ad app credential reset --id $mailboxAppId --append --display-name dotMARC --years $SecretYears --query password -o tsv
$dashboardSecret = Invoke-Az ad app credential reset --id $dashboardAppId --append --display-name dotMARC --years $SecretYears --query password -o tsv

Write-Host @"

dotMARC app registrations are ready in tenant $tenantId.
Copy both client secrets now; they can't be shown again. Running this script again creates additional secrets.

Mailbox app (Graph:ClientId / Graph:ClientSecret):
  Client ID:     $mailboxAppId
  Client secret: $mailboxSecret

Dashboard app (EntraId:ClientId / EntraId:ClientSecret):
  Client ID:     $dashboardAppId
  Client secret: $dashboardSecret
"@

if ($DashboardRedirectUri) {
    Write-Host "Dashboard redirect URI: $DashboardRedirectUri"
}
else {
    Write-Host @'
Add a Web redirect URI ending in /signin-oidc to the dashboard app before sign-in.
For Azure Container Apps, its hostname is only known after deployment.
'@
}

$mailPermissions = 'Mail.ReadWrite'
if ($IncludeMailSend) {
    $mailPermissions += ' and Mail.Send'
}

Write-Host @"

The mailbox app has Microsoft Graph application permission(s): $mailPermissions.
Do not grant its admin consent until you have restricted it to the intended mailbox using the
Exchange Application Access Policy steps in the dotMARC deployment guide. Then grant consent with:

  az ad app permission admin-consent --id $mailboxAppId

The dashboard app has the delegated User.Read permission. If your tenant requires administrator
approval for sign-in, grant consent with:

  az ad app permission admin-consent --id $dashboardAppId
"@
