# InfraIsAlive — Design

Date: 2026-10-19
Status: Draft, pending owner approval

## 1. Intent

Several products (the first is an AI-powered website builder; more will follow) depend on Azure resources that are **not kept running 24x7** to save money. An existing schedule shuts them down outside business hours. When a customer uses a product at, say, 23:30, some dependencies are stopped and the product breaks.

InfraIsAlive is a small backend service that a product calls before doing work. It answers, for each named Azure resource the product depends on, "is it ready?". If a resource is stopped, it **starts it right away** and says so. The product polls the same call until everything is online, and is itself responsible for telling its customer to wait.

The service must cost as close to zero as possible. Its whole point is saving money.

The project will be published as **open source under the MIT license**. Documentation (deployment walkthrough and maintenance guides) is a first-class deliverable, not an afterthought: a stranger with an Azure subscription must be able to adopt it without talking to us.

## 2. Context (the owner's current environment)

- One VM running several Docker containers. The containers start automatically when the VM boots.
- One Azure Database for PostgreSQL Flexible Server (B2ms).
- Some containers will move to Azure Container Apps with scale-to-zero.
- The website-builder front end runs 24x7 in a Container App and will be the first caller.
- Shutdown is already handled by something else, and stays that way.

## 3. Goals

1. One endpoint that, given a list of resource names, returns the state of each one and starts any that are stopped.
2. Monitors **Azure resources only**, at the Azure control-plane level. For a VM, "online" means the VM is running, not that the workloads inside it are healthy.
3. Adding support for a new Azure resource type takes **configuration only, no code change**.
4. Near-zero running cost. Estimated under US$1/month at the expected usage.
5. Deployable end to end with `azd up`.
6. Callers authenticate with Azure managed identities. No shared secrets.
7. Documentation good enough for an outside adopter to deploy, configure, extend, and troubleshoot it alone.

## 4. Non-goals

- **Never stops or deallocates anything.** It only wakes resources.
- No inspection of what runs inside a resource (containers in a VM, databases in a server, app health endpoints).
- No UI. Callers are backend services.
- No per-product authorization. Any allowed caller may query and start any registered resource.
- No "groups" or "sets" of resources. Callers ask for individual named resources.
- No check-only mode. Every call wakes stopped resources (owner's decision, accepted knowing a monitoring probe would wake things).
- No timeout or "stuck starting" tracking. That would need persisted state. Each caller decides how long it is willing to wait.
- No caching. Every call reads live state from Azure.
- No anonymous or shared-secret callers. Managed identity is the intended caller identity. A caller outside Azure would need its own Entra service principal added to the allowlist; this works but is not the documented path (see 7.4).
- No scheduling of any kind.

## 5. Anti-goals (would be a failure even if it works)

- The service itself becomes a noticeable line on the bill: anything always-on, a database, extra paid resources beyond what Flex Consumption requires.
- Supporting a new resource type requires editing C#.
- Any environment-coupled value (subscription, resource IDs, ARM endpoint, allowed callers, API versions) hardcoded in code.
- Resources woken accidentally by intermediaries. This is why the endpoint is POST, not GET.
- Docs that only work for the original author's environment.

## 6. Decided by the owner

| Topic | Decision |
|---|---|
| Hosting | Azure Functions, **Flex Consumption** plan |
| Language | **C# / .NET** |
| License | **MIT**, public open source |
| Caller authentication | **Managed identity** of each calling product, checked against an allowlist in configuration |
| Authorization | Any allowed caller can query and start any registered resource |
| Endpoint surface | **A single endpoint** that always wakes stopped resources |
| Resource-type support | **Config-driven profiles**, ships with built-in profiles for VM, PostgreSQL Flexible Server, Container Apps |
| Deployment | **Bicep + `azd up`** |
| Scope of "online" | Azure resource level only |
| Shutdown | Out of scope, never performed |

### Why Flex Consumption (recorded for future readers)

Compared with a scale-to-zero Container App: the Container Apps free grant (180k vCPU-s/month) is per subscription and is likely already used up by the owner's 24x7 app. Each call would also keep a replica alive for the cooldown period (~5 minutes). If products call frequently during business hours, that comes to about US$7/month. Flex Consumption bills only execution time (minimum 1 s per execution) with a per-subscription free grant of 100,000 GB-s and 250,000 executions/month. The only extras are the mandatory storage account (cents) and optional Application Insights.

## 7. How it behaves

### 7.1 The endpoint

`POST /api/ensure`

Request:

```json
{ "names": ["x", "z", "w"] }
```

For each name, in parallel and independently:

| Live Azure state | Action | Reported `state` | `startTriggered` |
|---|---|---|---|
| Running / ready | none | `Online` | false |
| Starting | none | `Starting` | false |
| Stopped / deallocated | issue the start action | `Starting` | true |
| Stopped, but the profile has no start action | none | `Stopped` | false |
| Stopping (shutdown in progress) | none, cannot start yet | `Stopped` | false |
| Start action rejected by Azure, or resource reported failed | — | `Failed` | false (`startTriggered` is true only when Azure accepted a start in this call) |
| Name not registered, or raw value not in the profile's map | none | `Unknown` | false |

Response: HTTP 200 with one entry per requested name:

```json
{
  "results": [
    { "name": "x", "state": "Online",   "startTriggered": false, "rawState": "PowerState/running", "checkedAt": "2026-10-19T23:30:01Z" },
    { "name": "z", "state": "Starting", "startTriggered": true,  "rawState": "Stopped",            "checkedAt": "2026-10-19T23:30:01Z" },
    { "name": "w", "state": "Unknown",  "startTriggered": false, "message": "Resource 'w' is not registered", "checkedAt": "2026-10-19T23:30:01Z" }
  ]
}
```

- `message` appears when there is something to explain (Failed, Unknown, Stopping).
- A problem with one resource never fails the whole call. Non-200 is only for a malformed request (400) or authentication/authorization failure (401/403).
- The start action is fire-and-forget. The call returns immediately and does not wait for the resource to become ready.
- Calling repeatedly is safe. A resource already starting is not started again. If two callers race and Azure answers the second start with a conflict, that is reported as `Starting`, not `Failed`.
- Intended caller pattern: call, and if anything is not `Online`, tell the customer to wait and call again every few seconds until all are `Online` or the caller's own patience runs out.

### 7.2 Configuration model

Two parts, both configuration, never code.

**Resource-type profiles** describe how to read and start one kind of Azure resource through the generic Azure Resource Manager (ARM) REST API:

- `ApiVersion`: ARM API version for that resource provider.
- `StatusPath`: suffix appended to the resource ID for the status read (empty for the resource itself, `/instanceView` for VMs).
- `StatusField`: path expression selecting the raw state value in the JSON response. It must support array filtering, because the VM power state lives in `statuses[]` next to other entries.
- `StartAction`: action name POSTed as `{resourceId}/{StartAction}?api-version=...`. Omit it for status-only types.
- `StateMap`: raw value → one of `Online`, `Starting`, `Stopped`, `Stopping`, `Failed`. `Stopping` is internal and is reported as `Stopped` (see table above).
- `RequiredActions`: the RBAC actions the Function's identity needs for this type (for example `Microsoft.Compute/virtualMachines/read`, `Microsoft.Compute/virtualMachines/instanceView/read`, `Microsoft.Compute/virtualMachines/start/action`). The deployment builds the custom role from these (see 7.4), so a new profile stays config-only.

**Registered resources** map a friendly name to a profile and a full ARM resource ID:

```json
"Resources": {
  "x": { "Type": "vm", "ResourceId": "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Compute/virtualMachines/<vm>" },
  "z": { "Type": "postgres-flexible", "ResourceId": "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.DBforPostgreSQL/flexibleServers/<server>" }
}
```

Built-in profiles ship in the repository for:

- `vm` (Microsoft.Compute/virtualMachines; power state from instance view; `running`/`starting`/`stopped`/`deallocated`/`stopping`/`deallocating`)
- `postgres-flexible` (Microsoft.DBforPostgreSQL/flexibleServers; `properties.state`)
- `container-app` (Microsoft.App/containerApps; `properties.runningStatus`). A Container App that is running at the Azure level counts as `Online` even with zero replicas, because it wakes on its first request.

The API versions, field paths, and raw values in the built-in profiles **must be verified against real ARM responses** before release. The values used during design were illustrative only.

Built-in profiles must be overridable, and adopters must be able to add new ones without touching code. Registered resources are environment-specific and must never be committed in the upstream repository (ship an example file instead).

### 7.3 Talking to Azure

- Use the Function's own managed identity to call ARM.
- The ARM endpoint/authority comes from configuration, so sovereign clouds can work.
- Reads and start actions are generic REST calls driven by the profile. No per-type SDK client.
- Azure Resource Graph was considered and rejected: its data can lag by minutes, which breaks the Starting → Online transition the callers poll for.

### 7.4 Security

- Callers: Microsoft Entra ID authentication on the Function. The token audience is an Entra app registration representing the Function (see 8 for who creates it). Callers obtain a token for that audience with their managed identity. Callers must never send ARM-audience tokens.
- Allowlist (my call): a list of Entra **object IDs** in configuration, set at deploy time. Any principal type on the list is accepted (managed identity, service principal, or user). Managed identity is the documented and recommended path; accepting other types is what lets the walkthrough verify a deployment with `az account get-access-token` from the deployer's own login (see 9.2).
- The Function's identity gets **least privilege** (my call): a **custom role definition** built from the `RequiredActions` of the profiles in use, assigned at the scope of each registered resource. Built-in roles such as Virtual Machine Contributor are not used, because they also allow delete. The deployment creates the role definition and the assignments from the configuration. The docs must list the actions each built-in profile needs and explain how to work out the actions for a new profile.
- No secrets in code, config files, or app settings.

## 8. Deployment

- `azd up` provisions everything with Bicep: the Flex Consumption Function App and its storage account, managed identity, app settings from configuration, role assignments on each registered resource, the caller allowlist, and optionally Application Insights.
- The adopter fills in their configuration (registered resources, allowed callers, region) before running `azd up`. Re-running after a config change applies it, including new role assignments.
- Removing a resource from the configuration must remove its role assignment on the next `azd up` (my call: leftover grants contradict least privilege). The mechanism (deployment stacks or a cleanup hook) is the builder's choice; the maintenance guide documents it.
- Creating the custom role definition needs a subscription-scope deployment. The deploying person needs Owner or User Access Administrator on the subscription.
- **Entra app registration (owner's decision):** by default `azd up` creates the app registration used as the token audience (for example via the Microsoft Graph Bicep extension or an azd hook). This needs a directory role such as Application Developer. Adopters without directory rights can instead pass the client ID of a registration an administrator created beforehand, and `azd up` uses it without trying to create one. The walkthrough documents both paths and the permissions each needs.
- Registered resources may live in other resource groups than the Function. The deployment must handle role assignments across resource groups. Cross-subscription support is desirable but not required for v1; document whichever is implemented.
- Application Insights is optional via a deployment parameter (my call: enabled by default with a low daily cap, since the troubleshooting guide relies on it).

## 9. Documentation deliverables

1. `README.md`: what it is, the problem it solves, cost profile, the one endpoint, quick start.
2. **Deployment walkthrough**: prerequisites (azd, Azure CLI, .NET SDK, required Azure roles for the person deploying, since creating role assignments needs Owner or User Access Administrator), filling in config, `azd up`, verifying with a first call. The verification step temporarily adds the deployer's own object ID to the allowlist, calls the endpoint with a token from `az account get-access-token`, then removes it.
3. **Registering a resource and allowing a caller**: including how a calling Container App acquires a token with its managed identity, with a minimal C# client example.
4. **Adding a new resource type**: how to find the ARM API version, the status field and its raw values, and the start action, how to work out the RBAC permission, and how to test the new profile.
5. **Permissions reference**: the RBAC actions each built-in profile needs.
6. **Troubleshooting and maintenance**: reading logs, common failures (auth rejected, missing permission, regional capacity preventing a start, unmapped raw state), upgrading .NET/Functions runtime, rotating the caller allowlist.
7. `LICENSE` (MIT), `CONTRIBUTING.md`.

## 10. Known risks

- **Starting can fail for capacity reasons.** Microsoft documents that starting a stopped PostgreSQL Flexible Server is not guaranteed during regional capacity shortages. This surfaces as `Failed` or as a long `Starting`. Callers must have their own wait limit.
- **Wake latency is minutes, not seconds.** VMs typically take 1–3 minutes and PostgreSQL several minutes. The Function's own cold start is negligible next to that.
- **Every call wakes things.** A health probe or dashboard pointed at the endpoint will start stopped resources. Document this prominently.
- **Free grants are per subscription** and shared with any other Flex Consumption apps the adopter runs.

## 11. Left to the builder

- Solution and repository layout, project names, test project structure.
- Exact .NET version (current LTS supported by Flex Consumption) and Functions worker model (isolated).
- Mechanism for supplying profiles and resources (app settings, a bundled JSON file, Bicep parameters), as long as it meets section 7.2's rules and the walkthrough stays simple.
- The path-expression syntax for `StatusField` and the library behind it.
- How the caller allowlist is enforced (built-in App Service authentication settings vs. validation in code).
- HTTP client details: retries on transient ARM errors, timeouts, parallelism.
- Logging shape and correlation.
- Tests: at minimum, profile evaluation and state mapping against recorded real ARM JSON samples for each built-in profile, and the per-row behavior in the 7.1 table.

## 12. Validation environment (owner's decision)

Built-in profiles are validated, and ARM samples recorded, against **temporary, low-cost resources created in the owner's subscription for the test session only**: a small VM, a PostgreSQL Flexible Server on a Burstable tier (e.g. B1ms), and a Container App. They are stopped and started to capture every state, then deleted. The owner's production resources (including the B2ms server) are never touched. Recorded samples must be scrubbed of subscription IDs and other identifying values before being committed.
