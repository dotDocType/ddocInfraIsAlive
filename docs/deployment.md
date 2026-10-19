# Deployment walkthrough

This guide takes you from an empty subscription to a working `POST /api/ensure` call.

## 1. Prerequisites

Tools:

- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- [Azure CLI (`az`)](https://learn.microsoft.com/cli/azure/install-azure-cli)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

Permissions for the person deploying:

- **Owner** or **User Access Administrator** on the subscription. The deployment creates a custom role and role
  assignments.
- One of the following, for the Entra app registration that callers request tokens for:
  - **Default path:** a directory role that can create app registrations, such as **Application Developer**.
    `azd up` then creates the registration for you.
  - **Pre-created path:** an administrator creates the registration once (see [section 3](#3-optional-use-a-pre-created-app-registration))
    and you pass its client ID.

> **Personal Microsoft accounts** (for example an outlook.com or gmail-based Microsoft account) cannot deploy
> Microsoft Graph resources from Bicep. If you sign in to Azure with one, use the pre-created path.

Region: pick one that supports Flex Consumption:

```sh
az functionapp list-flexconsumption-locations -o table
```

v1 limitation: every registered resource must be in the **same subscription** as the deployment. Resource groups
can differ.

## 2. Configure

```sh
cp config/infraisalive.example.json config/infraisalive.json
```

Edit `config/infraisalive.json`. It is git-ignored, so your resource IDs never reach the repository.

```json
{
  "AllowedCallerObjectIds": ["<object id of each calling product's managed identity>"],
  "ResourceTypes": {},
  "Resources": {
    "app-vm":  { "Type": "vm", "ResourceId": "/subscriptions/.../providers/Microsoft.Compute/virtualMachines/my-vm" },
    "main-db": { "Type": "postgres-flexible", "ResourceId": "/subscriptions/.../providers/Microsoft.DBforPostgreSQL/flexibleServers/my-db" }
  }
}
```

- `Resources` maps the names your products send to full ARM resource IDs. Find an ID with
  `az resource show -g <rg> -n <name> --resource-type <type> --query id -o tsv`.
- `Type` is a profile key. The built-in profiles are `vm`, `postgres-flexible` and `container-app` (see
  `config/profiles.builtin.json`). You can add your own in `ResourceTypes` (see [adding-a-resource-type.md](adding-a-resource-type.md)).
- `AllowedCallerObjectIds` are Entra object IDs. **An empty list rejects every caller.** See
  [registering-resources-and-callers.md](registering-resources-and-callers.md) for how to find them.

Optional settings for the azd environment:

| Setting | Default | Meaning |
|---|---|---|
| `EXISTING_API_CLIENT_ID` | *(empty)* | Client ID of a pre-created app registration. When empty, `azd up` creates one. |
| `ENABLE_APP_INSIGHTS` | `true` | `false` skips Log Analytics and Application Insights. |
| `APP_INSIGHTS_DAILY_CAP_GB` | `0.1` | Daily ingestion cap of the Log Analytics workspace. |

Set them with `azd env set <NAME> <value>` after `azd env new` (or after the first `azd up` prompt).

## 3. (Optional) Use a pre-created app registration

Skip this section if your account can create app registrations.

An administrator runs the commands below once. They reproduce what `infra/app/entra-app.bicep` does: v2 access
tokens, a `user_impersonation` scope, and the Azure CLI pre-authorized for it, so the verification step in
section 5 works without a consent prompt.

```sh
appId=$(az ad app create --display-name "InfraIsAlive API" --sign-in-audience AzureADMyOrg --query appId -o tsv)
objectId=$(az ad app show --id "$appId" --query id -o tsv)
scopeId=$(uuidgen)

# 1) token version + scope
az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$objectId" --body "{
  \"api\": {
    \"requestedAccessTokenVersion\": 2,
    \"oauth2PermissionScopes\": [{
      \"id\": \"$scopeId\", \"value\": \"user_impersonation\", \"type\": \"User\", \"isEnabled\": true,
      \"adminConsentDisplayName\": \"Call InfraIsAlive\", \"adminConsentDescription\": \"Allows calling the InfraIsAlive ensure endpoint as the signed-in user.\",
      \"userConsentDisplayName\": \"Call InfraIsAlive\", \"userConsentDescription\": \"Allows calling the InfraIsAlive ensure endpoint as you.\"
    }]
  }
}"

# 2) pre-authorize the Azure CLI (04b07795-8ddb-461a-bbee-02f9e1bf7b46) for that scope
az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$objectId" --body "{
  \"api\": { \"preAuthorizedApplications\": [{ \"appId\": \"04b07795-8ddb-461a-bbee-02f9e1bf7b46\", \"delegatedPermissionIds\": [\"$scopeId\"] }] }
}"

az ad sp create --id "$appId"
echo "$appId"
```

On a sovereign cloud, replace `graph.microsoft.com` with that cloud's Graph endpoint.

Then pass the ID to your environment:

```sh
azd env set EXISTING_API_CLIENT_ID <appId>
```

## 4. Deploy

```sh
azd auth login
azd up
```

`azd up` runs these steps in order:

1. `scripts/check-config` checks that `config/infraisalive.json` exists, lists at least one caller, and has no
   leftover placeholders.
2. Bicep creates `rg-<environment>` with the Flex Consumption app, storage, a user-assigned managed identity,
   optional monitoring, and (on the default path) the app registration.
3. `tools/InfraIsAlive.RbacSync` creates the custom role and assigns it on every registered resource. On the first
   run, expect a few "Waiting for Entra/ARM replication" lines while the new identity propagates.
4. The Function code is deployed, with your config file inside the package.

Useful outputs:

```sh
azd env get-value FUNCTION_URL
azd env get-value API_AUDIENCE
azd env get-value FUNCTION_PRINCIPAL_ID
```

## 5. Verify with a first call

Managed identities only exist inside Azure, so verify with your own user account temporarily added to the
allowlist.

```sh
# 1) add yourself
az ad signed-in-user show --query id -o tsv
#    paste that ID into AllowedCallerObjectIds in config/infraisalive.json, then:
azd provision && azd deploy

# 2) call it
token=$(az account get-access-token --scope "$(azd env get-value API_AUDIENCE)/.default" --query accessToken -o tsv)
curl -s -X POST "$(azd env get-value FUNCTION_URL)/api/ensure" \
  -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
  -d '{ "names": ["app-vm", "main-db"] }'

# 3) remove yourself from AllowedCallerObjectIds and apply again
azd provision && azd deploy
```

Each name should come back with a `state`. Anything stopped comes back as `Starting` with `"startTriggered": true`.
If the call fails, see [troubleshooting.md](troubleshooting.md).

## 6. Changing the configuration later

Edit `config/infraisalive.json` and run `azd up` again. Permissions follow the config: new resources get an
assignment, removed resources lose theirs. Allowlist changes apply both to Easy Auth and to the in-code check.

## 7. Removing everything

```sh
azd down --purge
```

This deletes the resource group and its contents. Three things live outside that resource group and stay
behind: the role assignments on your registered resources, the custom role, and (on the default path) the app
registration. Note the two IDs **before** running `azd down`, then remove the rest:

```sh
principal=$(azd env get-value FUNCTION_PRINCIPAL_ID)
apiClientId=$(azd env get-value API_CLIENT_ID)
azd down --purge

role="InfraIsAlive Resource Waker ($(echo "$principal" | cut -c1-8))"
az role assignment list --role "$role" --all --query "[].id" -o tsv | xargs -n1 az role assignment delete --ids
az role definition delete --name "$role"
az ad app delete --id "$apiClientId"   # default path only
```
