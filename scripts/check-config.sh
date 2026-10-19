#!/bin/sh
# Guard run by azd before provision/deploy: the adopter config must exist and allow at least one caller.
set -eu

config="$(dirname "$0")/../config/infraisalive.json"

if [ ! -f "$config" ]; then
  echo "config/infraisalive.json not found. Copy config/infraisalive.example.json to config/infraisalive.json and fill it in." >&2
  exit 1
fi

if tr -d ' \t\r\n' < "$config" | grep -q '"AllowedCallerObjectIds":\[\]'; then
  echo "config/infraisalive.json: AllowedCallerObjectIds is empty, so every caller would be rejected. Add the object ID of at least one caller." >&2
  exit 1
fi

if grep -q '<object-id\|<subscription-id>\|<resource-group>' "$config"; then
  echo "config/infraisalive.json still contains placeholders from the example file. Replace them with real values." >&2
  exit 1
fi
