# Permissions reference

InfraIsAlive works with three identities. Each one gets the least it needs.

## The Function's managed identity

A user-assigned managed identity, created by Bicep, used for two things:

1. **Storage**: *Storage Blob Data Owner* on the Function's own storage account, which Flex Consumption needs for
   the deployment package and the host.
2. **Target resources**: a **custom role** named `InfraIsAlive Resource Waker (<first 8 chars of the principal ID>)`,
   assigned **on each registered resource** (not on the subscription or resource group).

Built-in roles such as *Virtual Machine Contributor* are deliberately not used, because they can also delete or
reconfigure the resource.

### Actions per built-in profile

| Profile | Actions |
|---|---|
| `vm` | `Microsoft.Compute/virtualMachines/read`, `Microsoft.Compute/virtualMachines/instanceView/read`, `Microsoft.Compute/virtualMachines/start/action` |
| `postgres-flexible` | `Microsoft.DBforPostgreSQL/flexibleServers/read`, `Microsoft.DBforPostgreSQL/flexibleServers/start/action` |
| `container-app` | `Microsoft.App/containerApps/read`, `Microsoft.App/containerApps/start/action` |

The custom role's actions are the union of the `RequiredActions` of **the profiles your registered resources
actually use**. For a profile of your own, see [adding-a-resource-type.md](adding-a-resource-type.md#step-by-step).

### How the role is kept in sync

`tools/InfraIsAlive.RbacSync` runs as the azd `postprovision` hook, signed in as you:

1. Creates or updates the custom role (assignable at the subscription scope).
2. Ensures one assignment per registered resource. IDs are deterministic, so re-runs are idempotent.
3. Lists the identity's assignments and **deletes this role's assignments on resources that are no longer
   registered**. Assignments of other roles are never touched.

To run it manually:

```sh
dotnet run --project tools/InfraIsAlive.RbacSync -- \
  --subscription-id "$(azd env get-value AZURE_SUBSCRIPTION_ID)" \
  --principal-id "$(azd env get-value FUNCTION_PRINCIPAL_ID)" \
  --arm-endpoint "$(azd env get-value ARM_ENDPOINT)" \
  --config-dir ./config
```

**v1 limitation:** all registered resources must be in the deployment's subscription. `RbacSync` stops with an
error naming any resource that isn't.

## The person deploying

- **Owner** or **User Access Administrator** on the subscription, to create the custom role and the assignments.
- On the default path, a directory role that can create app registrations (for example *Application Developer*).
  Otherwise, use a pre-created registration (see [deployment.md](deployment.md#3-optional-use-a-pre-created-app-registration)).

## Callers

Callers need **no Azure RBAC role at all**. They need:

1. A managed identity (or another Entra principal) whose **object ID** is in `AllowedCallerObjectIds`.
2. A token for the audience `API_AUDIENCE` (the app registration's client ID), requested as `<client-id>/.default`.

> **Never disable Authentication (Easy Auth) on the Function app.** The in-code allowlist trusts the
> `X-MS-CLIENT-PRINCIPAL-ID` header that Easy Auth sets and strips from incoming requests. Without Easy Auth,
> anyone who knows the URL can forge that header and start your resources.

Managed identities get app-only tokens without consent. For a user to get a token (as in the deployment
verification step), the registration must expose the `user_impersonation` scope with the Azure CLI pre-authorized.
The default path sets this up.
