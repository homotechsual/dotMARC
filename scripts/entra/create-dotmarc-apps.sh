#!/usr/bin/env bash
# Creates or reuses dotMARC's mailbox and dashboard Entra app registrations, then prints their
# client IDs and newly-created secrets. Run with Bash and an Azure CLI signed in to the target tenant.
#
# Usage: create-dotmarc-apps.sh [--dashboard-redirect-uri <uri>] [--include-mail-send]
#                              [--secret-years <years>]
set -euo pipefail

readonly GRAPH_APP_ID="00000003-0000-0000-c000-000000000000"
readonly MAIL_READ_WRITE_ID="e2a3a72e-5f79-4c64-b1b1-878b674786c9"
readonly MAIL_SEND_ID="b633e1c5-b582-4048-a93e-9f11b44c7e96"
readonly USER_READ_ID="e1fe6dd8-ba31-4d61-89e7-88639da4683d"

usage() {
  echo "Usage: $0 [--dashboard-redirect-uri <uri>] [--include-mail-send] [--secret-years <years>]" >&2
  echo "  e.g. $0 --dashboard-redirect-uri https://dotmarc.example.com/signin-oidc" >&2
  exit 2
}

dashboard_redirect_uri=""
include_mail_send=false
secret_years=2
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dashboard-redirect-uri)
      [[ $# -ge 2 ]] || usage
      dashboard_redirect_uri="$2"
      shift 2
      ;;
    --include-mail-send)
      include_mail_send=true
      shift
      ;;
    --secret-years)
      [[ $# -ge 2 ]] || usage
      secret_years="$2"
      shift 2
      ;;
    -h|--help) usage ;;
    *) echo "Unknown option: $1" >&2; usage ;;
  esac
done
[[ "$secret_years" =~ ^[1-9][0-9]*$ ]] || {
  echo "--secret-years must be a whole number of years." >&2
  exit 2
}
if [[ -n "$dashboard_redirect_uri" && "$dashboard_redirect_uri" != https://*/signin-oidc ]]; then
  echo "--dashboard-redirect-uri must be an https:// URI ending in /signin-oidc." >&2
  exit 2
fi

az_tsv() {
  az "$@" | tr -d '\r'
}

command -v az >/dev/null || {
  echo "The Azure CLI (az) isn't installed. Azure Cloud Shell has it ready to use." >&2
  exit 1
}
tenant_id=$(az_tsv account show --query tenantId -o tsv 2>/dev/null) || {
  echo "Sign in to the target tenant first with: az login" >&2
  exit 1
}
[[ -n "$tenant_id" ]] || {
  echo "Azure CLI returned no tenant ID. Sign in to the target tenant with: az login" >&2
  exit 1
}

find_app_id() {
  local display_name="$1"
  local app_ids
  app_ids=$(az_tsv ad app list --display-name "$display_name" --query "[].appId" -o tsv)
  if [[ $(wc -w <<<"$app_ids") -gt 1 ]]; then
    echo "More than one app registration is named '$display_name'. Rename the duplicates before continuing." >&2
    return 1
  fi
  printf '%s' "$app_ids"
}

create_or_reuse_app() {
  local display_name="$1"
  local app_id
  app_id=$(find_app_id "$display_name")
  if [[ -n "$app_id" ]]; then
    echo "Reusing the app registration '$display_name' ($app_id)." >&2
  else
    app_id=$(az_tsv ad app create --display-name "$display_name" --sign-in-audience AzureADMyOrg \
      --query appId -o tsv)
    echo "Created the app registration '$display_name' ($app_id)." >&2
  fi
  [[ -n "$app_id" ]] || {
    echo "Azure CLI returned no client ID for '$display_name'." >&2
    return 1
  }
  printf '%s' "$app_id"
}

ensure_permission() {
  local app_id="$1"
  local permission_id="$2"
  local permission_type="$3"
  local has_permission
  has_permission=$(az_tsv ad app show --id "$app_id" \
    --query "length(requiredResourceAccess[?resourceAppId=='$GRAPH_APP_ID'].resourceAccess[?id=='$permission_id'])" \
    -o tsv)
  if [[ "$has_permission" == "0" ]]; then
    az ad app permission add --id "$app_id" --api "$GRAPH_APP_ID" \
      --api-permissions "${permission_id}=${permission_type}" --output none
  fi
}

ensure_dashboard_redirect_uri() {
  local app_id="$1"
  [[ -n "$dashboard_redirect_uri" ]] || return 0

  local existing_redirect_uris
  local -a redirect_uris=()
  existing_redirect_uris=$(az_tsv ad app show --id "$app_id" --query "web.redirectUris[]" -o tsv)
  if [[ -n "$existing_redirect_uris" ]]; then
    mapfile -t redirect_uris <<<"$existing_redirect_uris"
  fi
  if ! printf '%s\n' "${redirect_uris[@]}" | grep -Fxq -- "$dashboard_redirect_uri"; then
    az ad app update --id "$app_id" --web-redirect-uris "${redirect_uris[@]}" "$dashboard_redirect_uri" \
      --output none
    echo "Added the dashboard redirect URI $dashboard_redirect_uri." >&2
  fi
}

mailbox_app_id=$(create_or_reuse_app "dotmarc-mailbox")
dashboard_app_id=$(create_or_reuse_app "dotmarc-dashboard")

az ad app update --id "$mailbox_app_id" --sign-in-audience AzureADMyOrg --output none
ensure_permission "$mailbox_app_id" "$MAIL_READ_WRITE_ID" Role
if [[ "$include_mail_send" == true ]]; then
  ensure_permission "$mailbox_app_id" "$MAIL_SEND_ID" Role
fi
az ad app update --id "$dashboard_app_id" --sign-in-audience AzureADMyOrg \
  --enable-id-token-issuance true --output none
ensure_dashboard_redirect_uri "$dashboard_app_id"
ensure_permission "$dashboard_app_id" "$USER_READ_ID" Scope

mailbox_secret=$(az_tsv ad app credential reset --id "$mailbox_app_id" --append \
  --display-name "dotMARC" --years "$secret_years" --query password -o tsv)
dashboard_secret=$(az_tsv ad app credential reset --id "$dashboard_app_id" --append \
  --display-name "dotMARC" --years "$secret_years" --query password -o tsv)

cat <<EOF

dotMARC app registrations are ready in tenant $tenant_id.
Copy both client secrets now; they can't be shown again. Running this script again creates additional secrets.

Mailbox app (Graph:ClientId / Graph:ClientSecret):
  Client ID:     $mailbox_app_id
  Client secret: $mailbox_secret

Dashboard app (EntraId:ClientId / EntraId:ClientSecret):
  Client ID:     $dashboard_app_id
  Client secret: $dashboard_secret
EOF

if [[ -n "$dashboard_redirect_uri" ]]; then
  echo "Dashboard redirect URI: $dashboard_redirect_uri"
else
  cat <<'EOF'
Add a Web redirect URI ending in /signin-oidc to the dashboard app before sign-in.
For Azure Container Apps, its hostname is only known after deployment.
EOF
fi

mail_permissions="Mail.ReadWrite"
if [[ "$include_mail_send" == true ]]; then
  mail_permissions+=" and Mail.Send"
fi

cat <<EOF

The mailbox app has Microsoft Graph application permission(s): $mail_permissions.
Do not grant its admin consent until you have restricted it to the intended mailbox using the
Exchange Application Access Policy steps in the dotMARC Getting Started guide. Then grant consent with:

  az ad app permission admin-consent --id $mailbox_app_id

The dashboard app has the delegated User.Read permission. If your tenant requires administrator
approval for sign-in, grant consent with:

  az ad app permission admin-consent --id $dashboard_app_id
EOF
