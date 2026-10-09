#!/usr/bin/env bash
# Prepares a Google Cloud project for dotMARC's Google Cloud DNS push: enables the two APIs it calls, then prints the
# exact values and console links for the OAuth client, which gcloud can't create. Run it in Google Cloud Shell, or
# anywhere with bash and a signed-in gcloud CLI.
#
# Usage: prepare-google-cloud-dns.sh <dotmarc-host> <project-id>
#   e.g. prepare-google-cloud-dns.sh dotmarc.contoso.example contoso-dotmarc
set -euo pipefail

usage() {
  echo "Usage: $0 <dotmarc-host> <project-id>" >&2
  echo "  e.g. $0 dotmarc.contoso.example contoso-dotmarc" >&2
  exit 2
}

[[ $# -eq 2 && "$1" != -* ]] || usage

# Accept the host on its own or pasted as a URL.
host="${1#https://}"
host="${host#http://}"
host="${host%%/*}"
project_id="$2"
redirect_uri="https://${host}/dns-push/google-cloud-dns/callback"

command -v gcloud >/dev/null || { echo "The gcloud CLI isn't installed. Google Cloud Shell has it ready to use." >&2; exit 1; }
gcloud projects describe "$project_id" --format="value(projectId)" >/dev/null \
  || { echo "Can't find the project '$project_id', or you can't see it. Check the ID and that you're signed in (gcloud auth login)." >&2; exit 1; }

# Cloud DNS for the records; Resource Manager so dotMARC can find which of a customer's projects holds the zone.
gcloud services enable dns.googleapis.com cloudresourcemanager.googleapis.com --project "$project_id"
echo "Enabled the Cloud DNS and Cloud Resource Manager APIs in $project_id."

console="https://console.cloud.google.com/auth"
cat <<EOF

The rest is in the Google Cloud console, as gcloud can't create this kind of OAuth client:

1. Consent screen (skip if it's already set up):
   $console/branding?project=$project_id

2. Data access: add these two scopes:
   https://www.googleapis.com/auth/ndev.clouddns.readwrite
   https://www.googleapis.com/auth/cloudplatformprojects.readonly
   $console/scopes?project=$project_id

3. Clients: create a client, type Web application, with this authorized redirect URI:
   $redirect_uri
   $console/clients/create?project=$project_id

4. Audience: while the app is in Testing, add the accounts that will push as test users. Anyone else needs the
   app verified by Google first, which can take several days:
   $console/audience?project=$project_id

Then, in dotMARC, go to Manage > DNS push settings > Google Cloud DNS and enter the new client's ID and secret.
EOF
