<#
.SYNOPSIS
Creates the Entra app registration dotMARC uses to push records to Azure DNS, and prints the client ID and secret to
enter under Manage > DNS push settings.

.DESCRIPTION
The PowerShell equivalent of create-azure-dns-app.sh, for Windows, macOS and Linux. Needs the Azure CLI (az), signed in
with: az login. Azure Cloud Shell has both ready to use. Running it again with the same name reuses the app: it adds
the redirect URI if it's missing and issues an extra secret, leaving existing secrets working.

.PARAMETER DotmarcHost
Your dotMARC hostname, on its own or pasted as a URL.

.PARAMETER Name
The app registration's display name. Defaults to "dotMARC DNS push".

.PARAMETER SecretYears
How many years the new client secret lasts. Defaults to 2.

.EXAMPLE
./create-azure-dns-app.ps1 dotmarc.contoso.example
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$DotmarcHost,

    [string]$Name = 'dotMARC DNS push',

    [ValidateRange(1, 100)]
    [int]$SecretYears = 2
)

$ErrorActionPreference = 'Stop'

# Azure Service Management, and its delegated user_impersonation permission: the one permission dotMARC asks for.
$serviceManagementAppId = '797f4846-ba00-4fd7-ba43-dac1f8f63013'
$userImpersonationId = '41094075-9dad-400e-a0bd-54e686782033'

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

# Accept the host on its own or pasted as a URL.
$hostName = ($DotmarcHost -replace '^https?://', '') -replace '/.*$', ''
$redirectUri = "https://$hostName/dns-push/azure-dns/callback"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "The Azure CLI (az) isn't installed. Azure Cloud Shell has it ready to use."
}
try {
    $tenantId = Invoke-Az account show --query tenantId -o tsv
}
catch {
    throw 'Sign in first with: az login'
}

$existingAppIds = @((Invoke-Az ad app list --display-name $Name --query '[].appId' -o tsv) -split '\s+' | Where-Object { $_ })
if ($existingAppIds.Count -gt 1) {
    throw "More than one app registration is named '$Name'. Pass -Name to pick a different name."
}

if ($existingAppIds.Count -eq 1) {
    $appId = $existingAppIds[0]
    Write-Host "Reusing the app registration '$Name' ($appId)."
    # Pushes go into each customer's own tenant, so the app must accept accounts from any organisation.
    Invoke-Az ad app update --id $appId --sign-in-audience AzureADMultipleOrgs --output none | Out-Null
    $redirectUris = @((Invoke-Az ad app show --id $appId --query 'web.redirectUris[]' -o tsv) -split '\s+' | Where-Object { $_ })
    if ($redirectUris -notcontains $redirectUri) {
        $allUris = @($redirectUris) + $redirectUri
        Invoke-Az ad app update --id $appId --web-redirect-uris @allUris --output none | Out-Null
        Write-Host "Added the redirect URI $redirectUri."
    }
}
else {
    $appId = Invoke-Az ad app create --display-name $Name --sign-in-audience AzureADMultipleOrgs --web-redirect-uris $redirectUri --query appId -o tsv
    Write-Host "Created the app registration '$Name' ($appId)."
}

# The spaces around the pipe matter: they make PowerShell quote the argument, which keeps cmd.exe (az is a .cmd file on
# Windows) from treating the pipe as its own.
$permissionQuery = "length(requiredResourceAccess[?resourceAppId=='$serviceManagementAppId'].resourceAccess[] | [?id=='$userImpersonationId'])"
$existingPermissionCount = Invoke-Az ad app show --id $appId --query $permissionQuery -o tsv
if ($existingPermissionCount -eq '0') {
    # Delegated, so nothing is granted here: each customer consents when they first push.
    Invoke-Az ad app permission add --id $appId --api $serviceManagementAppId --api-permissions "$userImpersonationId=Scope" --output none | Out-Null
    Write-Host 'Added the delegated Azure Service Management user_impersonation permission.'
}

$secret = Invoke-Az ad app credential reset --id $appId --append --display-name dotMARC --years $SecretYears --query password -o tsv

Write-Host @"

Done. In dotMARC, go to Manage > DNS push settings > Azure DNS and enter:

  Client ID:     $appId
  Client secret: $secret

The secret expires in $SecretYears year(s) and can't be shown again, so copy it now.
Redirect URI:  $redirectUri
Home tenant:   $tenantId
"@
