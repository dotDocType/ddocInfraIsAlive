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

# Easy Auth caps allowedPrincipals.identities at 500 characters in total (about 13 object IDs).
ids=$(tr -d ' \t\r\n"' < "$config" | sed -n 's/.*AllowedCallerObjectIds:\[\([^]]*\)\].*/\1/p')
if [ "${#ids}" -gt 500 ]; then
  echo "config/infraisalive.json: AllowedCallerObjectIds totals ${#ids} characters; Easy Auth allows at most 500 (about 13 object IDs)." >&2
  exit 1
fi

if grep -q '<object-id\|<subscription-id>\|<resource-group>' "$config"; then
  echo "config/infraisalive.json still contains placeholders from the example file. Replace them with real values." >&2
  exit 1
fi
