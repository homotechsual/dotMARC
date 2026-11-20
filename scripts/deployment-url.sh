#!/usr/bin/env bash
set -euo pipefail

hostname="${1:-}"
label='[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?'
if [ "${#hostname}" -gt 253 ] || [[ ! "$hostname" =~ ^($label\.)+$label$ ]]; then
  echo "::error::Configure the deployment domain variable with a valid public hostname, without a scheme or path."
  exit 1
fi

hostname="${hostname,,}"
{
  echo "hostname=$hostname"
  echo "url=https://$hostname"
} >> "$GITHUB_OUTPUT"
