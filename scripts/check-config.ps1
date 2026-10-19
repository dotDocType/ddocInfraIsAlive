# Guard run by azd before provision/deploy: the adopter config must exist and allow at least one caller.
$ErrorActionPreference = 'Stop'

$config = Join-Path $PSScriptRoot '..' 'config' 'infraisalive.json'

if (-not (Test-Path $config)) {
    Write-Error 'config/infraisalive.json not found. Copy config/infraisalive.example.json to config/infraisalive.json and fill it in.'
    exit 1
}

$raw = Get-Content $config -Raw
$json = $raw | ConvertFrom-Json

if (-not $json.AllowedCallerObjectIds -or $json.AllowedCallerObjectIds.Count -eq 0) {
    Write-Error 'config/infraisalive.json: AllowedCallerObjectIds is empty, so every caller would be rejected. Add the object ID of at least one caller.'
    exit 1
}

# Easy Auth caps allowedPrincipals.identities at 500 characters in total (about 13 object IDs).
$joined = ($json.AllowedCallerObjectIds -join ',')
if ($joined.Length -gt 500) {
    Write-Error "config/infraisalive.json: AllowedCallerObjectIds totals $($joined.Length) characters; Easy Auth allows at most 500 (about 13 object IDs)."
    exit 1
}

if ($raw -match '<object-id|<subscription-id>|<resource-group>') {
    Write-Error 'config/infraisalive.json still contains placeholders from the example file. Replace them with real values.'
    exit 1
}
