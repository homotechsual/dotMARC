#!/usr/bin/env bash
# Creates the Entra app registration dotMARC uses to push records to Azure DNS, and prints the client ID and secret to
# enter under Manage > DNS push settings. Run it in Azure Cloud Shell (Bash), or anywhere with bash and a signed-in
# Azure CLI. Running it again with the same name reuses the app: it adds the redirect URI if it's missing and issues an
# extra secret, leaving existing secrets working.
#
# Usage: create-azure-dns-app.sh <dotmarc-host> [--name <display name>] [--secret-years <years>]
#   e.g. create-azure-dns-app.sh dotmarc.contoso.example
set -euo pipefail

# Azure Service Management, and its delegated user_impersonation permission: the one permission dotMARC asks for.
readonly SERVICE_MANAGEMENT_APP_ID="797f4846-ba00-4fd7-ba43-dac1f8f63013"
readonly USER_IMPERSONATION_ID="41094075-9dad-400e-a0bd-54e686782033"

usage() {
  echo "Usage: $0 <dotmarc-host> [--name <display name>] [--secret-years <years>]" >&2
  echo "  e.g. $0 dotmarc.contoso.example" >&2
  exit 2
}

host=""
display_name="dotMARC DNS push"
secret_years=2
while [[ $# -gt 0 ]]; do
  case "$1" in
    --name) [[ $# -ge 2 ]] || usage; display_name="$2"; shift 2 ;;
    --secret-years) [[ $# -ge 2 ]] || usage; secret_years="$2"; shift 2 ;;
    -h|--help) usage ;;
    -*) echo "Unknown option: $1" >&2; usage ;;
    *) [[ -z "$host" ]] || usage; host="$1"; shift ;;
  esac
done
[[ -n "$host" ]] || usage
[[ "$secret_years" =~ ^[1-9][0-9]*$ ]] || { echo "--secret-years must be a whole number of years." >&2; exit 2; }

# Accept the host on its own or pasted as a URL.
host="${host#https://}"
host="${host#http://}"
host="${host%%/*}"
redirect_uri="https://${host}/dns-push/azure-dns/callback"

command -v az >/dev/null || { echo "The Azure CLI (az) isn't installed. Azure Cloud Shell has it ready to use." >&2; exit 1; }
tenant_id=$(az account show --query tenantId -o tsv 2>/dev/null) || { echo "Sign in first with: az login" >&2; exit 1; }

existing_app_ids=$(az ad app list --display-name "$display_name" --query "[].appId" -o tsv)
if [[ $(wc -w <<<"$existing_app_ids") -gt 1 ]]; then
  echo "More than one app registration is named '$display_name'. Pass --name to pick a different name." >&2
  exit 1
fi

if [[ -n "$existing_app_ids" ]]; then
  app_id="$existing_app_ids"
  echo "Reusing the app registration '$display_name' ($app_id)."
  # Pushes go into each customer's own tenant, so the app must accept accounts from any organisation.
  az ad app update --id "$app_id" --sign-in-audience AzureADMultipleOrgs --output none
  mapfile -t redirect_uris < <(az ad app show --id "$app_id" --query "web.redirectUris[]" -o tsv)
  if [[ ! " ${redirect_uris[*]} " == *" ${redirect_uri} "* ]]; then
    az ad app update --id "$app_id" --web-redirect-uris "${redirect_uris[@]}" "$redirect_uri" --output none
    echo "Added the redirect URI $redirect_uri."
  fi
else
  app_id=$(az ad app create --display-name "$display_name" --sign-in-audience AzureADMultipleOrgs \
    --web-redirect-uris "$redirect_uri" --query appId -o tsv)
  echo "Created the app registration '$display_name' ($app_id)."
fi

has_permission=$(az ad app show --id "$app_id" \
  --query "length(requiredResourceAccess[?resourceAppId=='$SERVICE_MANAGEMENT_APP_ID'].resourceAccess[] | [?id=='$USER_IMPERSONATION_ID'])" -o tsv)
if [[ "$has_permission" == "0" ]]; then
  # Delegated, so nothing is granted here: each customer consents when they first push.
  az ad app permission add --id "$app_id" --api "$SERVICE_MANAGEMENT_APP_ID" \
    --api-permissions "${USER_IMPERSONATION_ID}=Scope" --output none 2>/dev/null
  echo "Added the delegated Azure Service Management user_impersonation permission."
fi

secret=$(az ad app credential reset --id "$app_id" --append --display-name "dotMARC" --years "$secret_years" \
  --query password -o tsv)

cat <<EOF

Done. In dotMARC, go to Manage > DNS push settings > Azure DNS and enter:

  Client ID:     $app_id
  Client secret: $secret

The secret expires in $secret_years year(s) and can't be shown again, so copy it now.
Redirect URI:  $redirect_uri
Home tenant:   $tenant_id
EOF
