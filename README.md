# InfraIsAlive

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Wake up the Azure resources your app needs, on demand, for almost nothing.**

Many teams stop VMs and databases outside business hours to save money. Then a customer shows up at 23:30 and
the product breaks. InfraIsAlive is a tiny Azure Function your products call before doing work. For each named
resource it answers *"is it ready?"*, and if the resource is stopped, it **starts it right away**. Your product
keeps calling until everything is `Online` and tells its user to wait in the meantime.

- **One endpoint**: `POST /api/ensure`.
- **Any Azure resource type** that has a start action, described in configuration with no code changes.
  Built-in profiles cover Virtual Machines, PostgreSQL Flexible Server and Container Apps.
- **Never stops anything.** Shutdown stays with whatever you use today.
- **No secrets.** Callers authenticate with their Azure managed identity.
- **Near-zero cost**: it runs on Azure Functions Flex Consumption (see [Cost](#cost)).

> **Every call wakes resources.** Don't point health probes, uptime monitors or dashboards at this endpoint:
> each call starts whatever is stopped, and that costs money.

## How it behaves

Request:

```http
POST https://<your-function>.azurewebsites.net/api/ensure
Authorization: Bearer <token for the InfraIsAlive app registration>
Content-Type: application/json

{ "names": ["app-vm", "main-db", "typo"] }
```

Response (HTTP 200):

```json
{
  "results": [
    { "name": "app-vm",  "state": "Online",   "startTriggered": false, "rawState": "PowerState/running", "checkedAt": "2026-10-19T23:30:01+00:00" },
    { "name": "main-db", "state": "Starting", "startTriggered": true,  "rawState": "Stopped",            "checkedAt": "2026-10-19T23:30:01+00:00" },
    { "name": "typo",    "state": "Unknown",  "startTriggered": false, "message": "Resource 'typo' is not registered.", "checkedAt": "2026-10-19T23:30:01+00:00" }
  ]
}
```

| Live Azure state | What InfraIsAlive does | `state` | `startTriggered` |
|---|---|---|---|
| Running / ready | nothing | `Online` | false |
| Starting | nothing | `Starting` | false |
| Stopped / deallocated | sends the start action | `Starting` | true |
| Stopped, profile has no start action | nothing | `Stopped` | false |
| Stopping (shutdown in progress) | nothing yet; a later call starts it | `Stopped` | false |
| Start rejected by Azure, or resource reported failed | — | `Failed` | false |
| Name not registered, status unreadable, or raw value not mapped | nothing | `Unknown` | false |

- `startTriggered` is `true` only when Azure accepted a start in *this* call.
- A problem with one resource never fails the whole call. Non-200 responses mean a malformed body (400), no
  token (401), or a caller that isn't on the allowlist (403).
- The start is fire-and-forget. Poll the same call every few seconds until all results are `Online`, and give up
  after a timeout of your own. VMs usually take 1–3 minutes and PostgreSQL several minutes. Starts can fail when the
  region is short on capacity.

## Quick start

1. Copy `config/infraisalive.example.json` to `config/infraisalive.json` and register your resources and callers.
2. Run `azd up`.

The full walkthrough is in **[docs/deployment.md](docs/deployment.md)**.

## Documentation

- [Deployment walkthrough](docs/deployment.md)
- [Registering resources and allowing callers](docs/registering-resources-and-callers.md), including a C# client
- [Adding a new resource type](docs/adding-a-resource-type.md)
- [Permissions reference](docs/permissions.md)
- [Troubleshooting and maintenance](docs/troubleshooting.md)
- [Contributing](CONTRIBUTING.md)

## Cost

| Item | Billing | Typical |
|---|---|---|
| Function executions | Flex Consumption free grant per subscription: 100,000 GB-s and 250,000 executions per month. At 512 MB, each call bills at least 0.5 GB-s. | **US$0** up to ~200k calls/month |
| Storage account | Required by Functions; holds the deployment package | cents |
| Application Insights (optional) | First 5 GB/month free per billing account; a 0.1 GB/day cap is set by default | **US$0** in normal use |

Free grants are shared with other Flex Consumption apps in the same subscription.

## How it works

- `src/InfraIsAlive.Functions`: the Function (.NET 10 isolated worker). Easy Auth validates the token and the code
  checks the caller's object ID against the allowlist.
- `src/InfraIsAlive.Core`: config loading, a generic Azure Resource Manager client, and profile evaluation
  (JSONPath to the raw state, then a state map).
- `tools/InfraIsAlive.RbacSync`: runs after `azd provision`. It creates a least-privilege custom role from the
  profiles in use, assigns it on each registered resource, and removes assignments for resources you unregister.
- `infra/`: Bicep for the Flex Consumption app, storage, the managed identity, optional monitoring, and
  (optionally) the Entra app registration.

## License

[MIT](LICENSE)
