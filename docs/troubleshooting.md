# Troubleshooting and maintenance

## HTTP errors from `/api/ensure`

| Status | Likely cause | Fix |
|---|---|---|
| **401** | No token, or Easy Auth rejected it: wrong audience, a v1 token on a registration that expects v2, or a token from another tenant. | Request the token for `<API_AUDIENCE>/.default`. Decode it (for example at jwt.ms) and check that `aud` is the client ID and `iss` ends with `/v2.0`. For a pre-created registration, set `requestedAccessTokenVersion` to 2. |
| **403** | The caller's object ID isn't in `AllowedCallerObjectIds`, or the list is empty. | Add the `oid` from the token to the list and run `azd up`. |
| **400** | The body isn't `{ "names": ["..."] }` with at least one non-blank string. | Fix the request. The response contains `error`. |
| **5xx right after deploy** | The Function failed to start. A missing setting or an invalid config file is the usual cause. | Check the logs (below). The startup error names the missing setting or lists every config error. |

## `Unknown` and `Failed` results

| `message` | Cause | Fix |
|---|---|---|
| `Resource 'x' is not registered.` | The name isn't a key under `Resources`. | Fix the name or register it. Matching ignores case and spaces. |
| `Status read failed: ARM returned 403.` | The Function's identity has no read permission on the resource. | Run `azd up` (or `RbacSync` manually) so the assignment is created. Permissions can take a few minutes to propagate. |
| `Status read failed: ARM returned 404.` | The resource was deleted or the `ResourceId` is wrong. | Fix or remove the entry. |
| `Status read failed: <exception>` | A network or timeout problem reaching ARM. | Usually transient; the next poll retries. |
| `Raw state '<value>' is not mapped by profile '<type>'.` | Azure returned a state the profile doesn't know. | Add the value to the profile's `StateMap`, or override the profile (see [adding-a-resource-type.md](adding-a-resource-type.md)). If `<value>` is `<none>`, `StatusField` matched nothing. |
| `Resource is stopping; it will be started by a later call once fully stopped.` | Shutdown was still in progress (reported as `Stopped`). | Nothing to fix. Keep polling and it will be started. |
| `Start already in progress.` | Another caller already started it (reported as `Starting`). | Nothing to fix. |
| `Start rejected: ARM returned <code>: ...` | Azure refused the start. Regional capacity shortages are a known cause for PostgreSQL Flexible Server, and quota limits are another. | Read the body; retry later or contact Azure support for capacity. |
| `Start failed: <exception>` | A network problem while sending the start. | Usually transient. |

## Deployment problems

- **"config/infraisalive.json not found / is empty / contains placeholders"**: the preprovision guard stopped
  you. Follow the message.
- **Graph or app-registration errors during `azd provision`**: your account can't create app registrations, or
  it is a personal Microsoft account. Use the pre-created path in [deployment.md](deployment.md#3-optional-use-a-pre-created-app-registration).
- **`Waiting for Entra/ARM replication...` lines from RbacSync**: normal on the first run, while the new identity
  or role propagates. It retries 6 times, 10 s apart, then fails. Re-running `azd provision` is safe.
- **`RbacSync` says a resource is in another subscription**: v1 supports only the deployment subscription.
- **`AuthorizationFailed` from RbacSync**: you need Owner or User Access Administrator on the subscription.

## Logs

With Application Insights enabled (the default), each call logs one line per resource:
`Ensure <name> for <principal>: <state> (raw ..., startTriggered ...) <message>`. Rejected callers are logged
with their principal ID.

In the portal, open the Application Insights resource in `rg-<environment>`, then **Logs**:

```kusto
traces
| where message startswith "Ensure " or message startswith "Rejected"
| order by timestamp desc
```

The default daily cap is 0.1 GB. If logs stop mid-day, the cap was reached; raise it with
`azd env set APP_INSIGHTS_DAILY_CAP_GB <value>` and `azd provision`.

## Maintenance

- **Upgrading .NET or the Functions runtime**: change `TargetFramework` in the `.csproj` files, `global.json`, and
  `runtime.version` in `infra/app/function.bicep` (check the supported versions with
  `az functionapp list-flexconsumption-runtimes --location <region> --runtime dotnet-isolated`), then `azd up`.
- **Rotating callers**: edit `AllowedCallerObjectIds` and run `azd up`. Easy Auth and the in-code check change
  together.
- **Updating a built-in profile** (for example a newer API version): edit `config/profiles.builtin.json`, update
  the fixtures and tests, and run `dotnet test`.
- **Package updates**: `dotnet list package --outdated`, update, `dotnet test`.
