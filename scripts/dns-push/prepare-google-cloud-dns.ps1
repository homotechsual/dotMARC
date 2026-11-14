<#
.SYNOPSIS
Prepares a Google Cloud project for dotMARC's Google Cloud DNS push: enables the two APIs it calls, then prints the
exact values and console links for the OAuth client, which gcloud can't create.

.DESCRIPTION
The PowerShell equivalent of prepare-google-cloud-dns.sh, for Windows, macOS and Linux. Needs the gcloud CLI, signed in
with: gcloud auth login. Google Cloud Shell has it ready to use.

.PARAMETER DotmarcHost
Your dotMARC hostname, on its own or pasted as a URL.

.PARAMETER ProjectId
The Google Cloud project to prepare.

.EXAMPLE
./prepare-google-cloud-dns.ps1 dotmarc.contoso.example contoso-dotmarc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$DotmarcHost,

    [Parameter(Mandatory, Position = 1)]
    [string]$ProjectId
)

$ErrorActionPreference = 'Stop'

# Accept the host on its own or pasted as a URL.
$hostName = ($DotmarcHost -replace '^https?://', '') -replace '/.*$', ''
$redirectUri = "https://$hostName/dns-push/google-cloud-dns/callback"

if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) {
    throw "The gcloud CLI isn't installed. Google Cloud Shell has it ready to use."
}

& gcloud projects describe $ProjectId '--format=value(projectId)' *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Can't find the project '$ProjectId', or you can't see it. Check the ID and that you're signed in (gcloud auth login)."
}

# Cloud DNS for the records; Resource Manager so dotMARC can find which of a customer's projects holds the zone.
& gcloud services enable dns.googleapis.com cloudresourcemanager.googleapis.com --project $ProjectId
if ($LASTEXITCODE -ne 0) {
    throw "Couldn't enable the APIs in $ProjectId."
}
Write-Host "Enabled the Cloud DNS and Cloud Resource Manager APIs in $ProjectId."

$console = 'https://console.cloud.google.com/auth'
Write-Host @"

The rest is in the Google Cloud console, as gcloud can't create this kind of OAuth client:

1. Consent screen (skip if it's already set up):
   $console/branding?project=$ProjectId

2. Data access: add these two scopes:
   https://www.googleapis.com/auth/ndev.clouddns.readwrite
   https://www.googleapis.com/auth/cloudplatformprojects.readonly
   $console/scopes?project=$ProjectId

3. Clients: create a client, type Web application, with this authorized redirect URI:
   $redirectUri
   $console/clients/create?project=$ProjectId

4. Audience: while the app is in Testing, add the accounts that will push as test users. Anyone else needs the
   app verified by Google first, which can take several days:
   $console/audience?project=$ProjectId

Then, in dotMARC, go to Manage > DNS push settings > Google Cloud DNS and enter the new client's ID and secret.
"@
