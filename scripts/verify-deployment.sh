#!/usr/bin/env bash
set -euo pipefail

url="${1:?Deployment URL is required}"
title="${2:?Expected page title is required}"

for attempt in {1..24}; do
  if html="$(curl --fail --silent --show-error --connect-timeout 5 --max-time 10 "$url/demo")" \
    && [[ "$html" == *"<title>$title</title>"* ]]; then
    echo "Verified $url"
    exit 0
  fi
  echo "Waiting for $url to serve the expected build (attempt $attempt/24)."
  if [ "$attempt" -lt 24 ]; then
    sleep 5
  fi
done

echo "::error::Deployment did not serve the expected page title: $title"
exit 1
