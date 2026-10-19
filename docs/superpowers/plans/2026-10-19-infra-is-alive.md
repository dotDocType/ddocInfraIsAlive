# InfraIsAlive Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A single-endpoint Azure Function (`POST /api/ensure`) that reports the state of named Azure resources and starts any that are stopped, driven entirely by configuration, deployable with `azd up`, and documented for outside adopters.

**Architecture:** A `Core` library holds the config model, profile evaluation (JSON path → raw state → mapped state), a thin generic ARM REST client, and the `EnsureService` that implements the spec's behavior table. A thin isolated-worker Function exposes it behind Easy Auth plus an in-code allowlist check. A small console tool (`RbacSync`), run as an azd `postprovision` hook, creates the custom role from the profiles' `RequiredActions` and reconciles one role assignment per registered resource (adding new, deleting removed). Bicep provisions the Flex Consumption app, storage, user-assigned identity, optional App Insights, and optionally the Entra app registration.

**Tech Stack:** .NET 10 (isolated worker, Azure Functions v4), xUnit, Azure.Identity, Newtonsoft.Json (JSONPath with filters via `SelectTokens`), System.Text.Json (config and HTTP payloads), Bicep + Microsoft Graph Bicep extension `v1.0:1.0.0`, Azure Developer CLI.

**Spec:** `docs/superpowers/specs/2026-10-19-infra-is-alive-design.md`

## Global Constraints

- Hosting: Azure Functions **Flex Consumption**, runtime `dotnet-isolated` `10.0`, instance memory **512 MB**, no always-ready instances.
- License MIT (already in repo). Everything written to files is English.
- **No environment-coupled literal in code**: ARM endpoint, managed identity client ID, subscription, resource IDs, allowed callers, API versions all come from config, app settings, or CLI args. Missing required setting → fail at startup with a message naming the setting. No `?? "default"` fallbacks.
- One endpoint only: `POST /api/ensure`, body `{ "names": [...] }`, Function route `ensure` with the default `api` prefix.
- Reported states, exact spelling: `Online`, `Starting`, `Stopped`, `Failed`, `Unknown`. Internal mapping targets: `Online`, `Starting`, `Stopped`, `Stopping`, `Failed`.
- Response JSON (camelCase, enums as strings): `{ "results": [ { "name", "state", "startTriggered", "rawState", "message", "checkedAt" } ] }`; `rawState`/`message` omitted when null; `checkedAt` ISO-8601 UTC.
- HTTP status: 200 for any well-formed authorized request (per-item problems live in the item); 400 malformed body or empty `names`; 401 no caller principal; 403 caller not on allowlist.
- `startTriggered` is `true` only when Azure accepted the start action in this call (decision recorded here: the spec's "true/false" for `Failed` resolves to `false`).
- Never stop, deallocate, or delete any target resource. No cache. No persisted state.
- Config files: built-in profiles `config/profiles.builtin.json` (committed); adopter file `config/infraisalive.json` (git-ignored) with committed `config/infraisalive.example.json`. An adopter profile with the same key **replaces** the built-in one entirely.
- Resource names are matched case-insensitively and trimmed; duplicate names in one request return one result (first occurrence order).
- Recorded ARM samples committed to the repo contain no real subscription IDs, tenant IDs, or resource names.

## Review Focus

1. **Empty or missing allowlist** — Easy Auth with an empty `allowedPrincipals.identities` lets any principal in the tenant through; the in-code check must deny everyone when the list is empty. (Task 5: `IsAllowed_EmptyAllowlist_DeniesEveryone`.)
2. **One resource's ARM read fails** (403 missing permission, 404 resource deleted, network exception) — that item becomes `Unknown` with a message carrying the status/exception, other items are unaffected. (Task 4: `Ensure_ReadThrows_OnlyThatItemUnknown`, `Ensure_Read403_UnknownWithStatusInMessage`.)
3. **Status field matches nothing** (e.g. a VM instance view with no `PowerState/*` entry while provisioning) — `Unknown`, `rawState` null, no start attempt. (Task 2: `Extract_NoMatch_ReturnsNull`; Task 4: `Ensure_RawStateMissing_Unknown`.)
4. **Sloppy names in the request** — `" X "`, `"x"` and `"X"` resolve to the same registered resource; duplicates collapse. (Task 4: `Ensure_NamesTrimmedCaseInsensitiveAndDeduped`.)
5. **Bad adopter config** — unknown `Type`, malformed `ResourceId`, empty `StateMap`, a state-map value outside the five targets, or a resource in a different subscription than the deployment — fails fast with every error listed, not a runtime surprise on the first call. (Task 1: `Load_InvalidConfig_ListsAllErrors`; Task 6: `Build_ResourceInOtherSubscription_Throws`.)

---

## File Structure

```
.gitignore                                   # bin/obj, config/infraisalive.json, .azure/
global.json                                  # pins .NET 10 SDK
InfraIsAlive.sln
config/profiles.builtin.json                 # vm, postgres-flexible, container-app
config/infraisalive.example.json             # adopter template
src/InfraIsAlive.Core/
  Configuration/ConfigModels.cs              # ResourceTypeProfile, RegisteredResource, InfraConfig, MappedState
  Configuration/ConfigLoader.cs              # load + merge + validate
  Configuration/InfraConfigException.cs
  Evaluation/StateExtractor.cs               # JSONPath → raw string
  Evaluation/StateMapper.cs                  # raw → MappedState?
  Arm/IArmClient.cs                          # IArmClient, ArmResponse
  Arm/ArmHttpClient.cs
  Ensure/EnsureResult.cs                     # ResourceState, EnsureResult
  Ensure/EnsureService.cs
src/InfraIsAlive.Functions/
  Program.cs
  EnsureFunction.cs
  CallerAuthorizer.cs
  EnsureRequestParser.cs
  host.json
  local.settings.example.json
tools/InfraIsAlive.RbacSync/
  Program.cs                                 # CLI args → RbacPlan → RbacSyncRunner
  RbacPlan.cs                                # DesiredRbac, deterministic GUIDs
  RbacSyncRunner.cs
tests/InfraIsAlive.Tests/
  Fixtures/*.json                            # ARM response samples
  FakeArmClient.cs
  ConfigLoaderTests.cs, StateEvaluationTests.cs, ArmHttpClientTests.cs,
  EnsureServiceTests.cs, FunctionTests.cs, RbacSyncTests.cs
infra/main.bicep, infra/main.parameters.json, infra/bicepconfig.json
infra/app/function.bicep, infra/app/entra-app.bicep, infra/app/monitoring.bicep
azure.yaml
scripts/check-config.sh, scripts/check-config.ps1
README.md, CONTRIBUTING.md
docs/deployment.md, docs/registering-resources-and-callers.md,
docs/adding-a-resource-type.md, docs/permissions.md, docs/troubleshooting.md
```

---

### Task 1: Solution scaffold and configuration loading

**Files:**
- Create: `.gitignore`, `global.json`, `InfraIsAlive.sln`, `src/InfraIsAlive.Core/InfraIsAlive.Core.csproj`, `src/InfraIsAlive.Core/Configuration/{ConfigModels,ConfigLoader,InfraConfigException}.cs`, `config/infraisalive.example.json`
- Test: `tests/InfraIsAlive.Tests/InfraIsAlive.Tests.csproj`, `tests/InfraIsAlive.Tests/ConfigLoaderTests.cs`

**Interfaces:**
- Produces (namespace `InfraIsAlive.Core.Configuration`):
  - `public enum MappedState { Online, Starting, Stopped, Stopping, Failed }`
  - `public sealed record ResourceTypeProfile(string ApiVersion, string StatusPath, string StatusField, string? StartAction, IReadOnlyDictionary<string, MappedState> StateMap, IReadOnlyList<string> RequiredActions)` — `StateMap` uses `StringComparer.OrdinalIgnoreCase`.
  - `public sealed record RegisteredResource(string Type, string ResourceId)`
  - `public sealed record InfraConfig(IReadOnlyList<string> AllowedCallerObjectIds, IReadOnlyDictionary<string, ResourceTypeProfile> ResourceTypes, IReadOnlyDictionary<string, RegisteredResource> Resources)` — both dictionaries `OrdinalIgnoreCase`.
  - `public static InfraConfig ConfigLoader.Load(string builtinProfilesPath, string adopterConfigPath)` — throws `InfraConfigException`.
  - `public sealed class InfraConfigException(IReadOnlyList<string> errors) : Exception` with `IReadOnlyList<string> Errors`; `Message` joins all errors.

JSON shapes (System.Text.Json, case-insensitive property names, enum strings):
- builtin file: `{ "ResourceTypes": { "<key>": { profile } } }`
- adopter file: `{ "AllowedCallerObjectIds": [], "ResourceTypes": { optional overrides/additions }, "Resources": { "<name>": { "Type": "...", "ResourceId": "..." } } }`

- [ ] **Step 1: Scaffold** — `global.json` pinning SDK `10.0.100` with `rollForward: latestFeature`; class library + xUnit test project on `net10.0`, `<Nullable>enable</Nullable>`; `.gitignore` covering `bin/`, `obj/`, `.azure/`, `config/infraisalive.json`, `local.settings.json`. `config/infraisalive.example.json` with two placeholder resources (`vm`, `postgres-flexible`) using `<subscription-id>`-style placeholders and one placeholder object ID.

- [ ] **Step 2: Write failing tests** in `ConfigLoaderTests.cs` (write temp files per test):

```csharp
[Fact] void Load_MergesBuiltinAndAdopter()
// builtin has "vm"; adopter adds "redis" and resource "x" of Type "vm"
// Assert: cfg.ResourceTypes.Keys ⊇ {"vm","redis"}; cfg.Resources["X"].Type == "vm" (case-insensitive lookup)

[Fact] void Load_AdopterProfileReplacesBuiltinEntirely()
// builtin "vm" RequiredActions = [a,b,c]; adopter "vm" RequiredActions = [a]
// Assert: cfg.ResourceTypes["vm"].RequiredActions SequenceEqual ["a"]

[Fact] void Load_StateMapIsCaseInsensitive()
// "Ready" → Online; Assert cfg.ResourceTypes["pg"].StateMap["READY"] == MappedState.Online

[Fact] void Load_InvalidConfig_ListsAllErrors()
// resource "a" Type "nope"; resource "b" ResourceId "not-an-id"; profile "p" with empty StateMap;
// profile "q" with StateMap value "Sleeping"; profile "r" with empty StatusField
// Assert: throws InfraConfigException; Errors.Count == 5; each error mentions its key ("a","b","p","q","r")

[Fact] void Load_MissingAdopterFile_ThrowsWithPath()
// Assert: InfraConfigException whose message contains the missing path
```

Validation rules: `Type` must exist in merged `ResourceTypes`; `ResourceId` must match `^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/.+/.+$` (case-insensitive); `ApiVersion`, `StatusField` non-empty; `StateMap` non-empty; `StatusPath` may be empty; a `StateMap` value that is not a `MappedState` name is an error, not a deserialization crash. `AllowedCallerObjectIds` may be empty (the Function denies all in that case — Task 5).

- [ ] **Step 3: Run** `dotnet test --filter ConfigLoaderTests` → FAIL (types missing).
- [ ] **Step 4: Implement** the models and `ConfigLoader.Load`. Deserialize to DTOs with string state values, collect every error, then build the records.
- [ ] **Step 5: Run** `dotnet test --filter ConfigLoaderTests` → PASS.
- [ ] **Step 6: Commit** `feat: add config model and loader`

---

### Task 2: Profile evaluation and built-in profiles

**Files:**
- Create: `src/InfraIsAlive.Core/Evaluation/{StateExtractor,StateMapper}.cs`, `config/profiles.builtin.json`
- Test: `tests/InfraIsAlive.Tests/StateEvaluationTests.cs`, `tests/InfraIsAlive.Tests/Fixtures/{vm-instanceview-running,vm-instanceview-deallocated,vm-instanceview-no-powerstate,postgres-ready,postgres-stopped,containerapp-running,containerapp-stopped}.json`

**Interfaces:**
- Consumes: `ResourceTypeProfile`, `MappedState`, `ConfigLoader.Load` (Task 1).
- Produces (namespace `InfraIsAlive.Core.Evaluation`):
  - `public static string? StateExtractor.Extract(string json, string statusField)` — Newtonsoft `JToken.Parse(json).SelectTokens(statusField)`, first token's string value; `null` when no match.
  - `public static MappedState? StateMapper.Map(ResourceTypeProfile profile, string? rawState)` — `null` when raw is null or not in `StateMap`.

Built-in profiles (illustrative values; Task 9 verifies against live ARM and corrects them):

| Key | ApiVersion | StatusPath | StatusField | StartAction | StateMap | RequiredActions |
|---|---|---|---|---|---|---|
| `vm` | `2024-07-01` | `/instanceView` | `$.statuses[?(@.code =~ /^PowerState\/.*$/)].code` | `start` | `PowerState/running`→Online, `PowerState/starting`→Starting, `PowerState/stopped`→Stopped, `PowerState/deallocated`→Stopped, `PowerState/stopping`→Stopping, `PowerState/deallocating`→Stopping | `Microsoft.Compute/virtualMachines/read`, `Microsoft.Compute/virtualMachines/instanceView/read`, `Microsoft.Compute/virtualMachines/start/action` |
| `postgres-flexible` | `2024-08-01` | `` | `$.properties.state` | `start` | `Ready`→Online, `Starting`→Starting, `Restarting`→Starting, `Updating`→Starting, `Stopped`→Stopped, `Stopping`→Stopping, `Disabled`→Failed, `Inaccessible`→Failed | `Microsoft.DBforPostgreSQL/flexibleServers/read`, `Microsoft.DBforPostgreSQL/flexibleServers/start/action` |
| `container-app` | `2024-03-01` | `` | `$.properties.runningStatus` | `start` | `Running`→Online, `Progressing`→Starting, `Stopped`→Stopped, `Degraded`→Failed | `Microsoft.App/containerApps/read`, `Microsoft.App/containerApps/start/action` |

Fixtures: hand-written from ARM docs shapes, with placeholder IDs (`00000000-0000-0000-0000-000000000000`, `rg-example`). The VM fixtures include a `ProvisioningState/succeeded` entry before the `PowerState/*` entry so the filter is actually exercised.

- [ ] **Step 1: Write failing tests:**

```csharp
[Theory]
[InlineData("vm-instanceview-running.json",     "vm", "PowerState/running",     MappedState.Online)]
[InlineData("vm-instanceview-deallocated.json", "vm", "PowerState/deallocated", MappedState.Stopped)]
[InlineData("postgres-ready.json",   "postgres-flexible", "Ready",   MappedState.Online)]
[InlineData("postgres-stopped.json", "postgres-flexible", "Stopped", MappedState.Stopped)]
[InlineData("containerapp-running.json", "container-app", "Running", MappedState.Online)]
[InlineData("containerapp-stopped.json", "container-app", "Stopped", MappedState.Stopped)]
void BuiltinProfile_ExtractsAndMaps(string fixture, string type, string expectedRaw, MappedState expected)
// loads config/profiles.builtin.json (copied to test output) + an empty adopter file

[Fact] void Extract_NoMatch_ReturnsNull()            // vm-instanceview-no-powerstate.json → null
[Fact] void Map_UnknownRawValue_ReturnsNull()        // "Hibernating" → null
[Fact] void Map_NullRaw_ReturnsNull()
```

- [ ] **Step 2: Run** `dotnet test --filter StateEvaluationTests` → FAIL.
- [ ] **Step 3: Implement** `StateExtractor`, `StateMapper`, write `config/profiles.builtin.json`; link `config/*.json` into the test project output.
- [ ] **Step 4: Run** `dotnet test --filter StateEvaluationTests` → PASS.
- [ ] **Step 5: Commit** `feat: add profile evaluation and built-in profiles`

---

### Task 3: Generic ARM client

**Files:**
- Create: `src/InfraIsAlive.Core/Arm/{IArmClient,ArmHttpClient}.cs`
- Test: `tests/InfraIsAlive.Tests/ArmHttpClientTests.cs`

**Interfaces:**
- Produces (namespace `InfraIsAlive.Core.Arm`):
  - `public sealed record ArmResponse(int StatusCode, string Body) { public bool IsSuccess => StatusCode is >= 200 and < 300; }`
  - `public interface IArmClient { Task<ArmResponse> SendAsync(HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken ct); }`
  - `public sealed class ArmHttpClient(HttpClient http, TokenCredential credential, Uri armEndpoint) : IArmClient` — requests `new Uri(armEndpoint, pathAndQuery)`, bearer token for scope `$"{armEndpoint.GetLeftPart(UriPartial.Authority)}/.default"`, `Content-Type: application/json` when body present, an empty JSON body `{}` is **not** added for bodiless POST (ARM start actions take no body).

- [ ] **Step 1: Write failing tests** (stub `HttpMessageHandler` capturing the request; stub `TokenCredential` recording the requested scopes and returning `"tok"`):

```csharp
[Fact] void Send_UsesConfiguredEndpointAndBearer()
// armEndpoint "https://management.example.test/"; SendAsync(Get, "/subscriptions/s/x?api-version=1", null)
// Assert: request.RequestUri == "https://management.example.test/subscriptions/s/x?api-version=1"
//         Authorization == "Bearer tok"; requested scope == "https://management.example.test/.default"

[Fact] void Send_ReturnsStatusAndBody_OnNonSuccess()   // handler returns 409 "conflict" → ArmResponse(409,"conflict"), no throw
[Fact] void Send_PostWithoutBody_HasNoContent()
[Fact] void Send_PutWithBody_SendsJson()
```

- [ ] **Step 2: Run** `dotnet test --filter ArmHttpClientTests` → FAIL.
- [ ] **Step 3: Implement** `ArmHttpClient`. Network exceptions propagate (callers handle them).
- [ ] **Step 4: Run** `dotnet test --filter ArmHttpClientTests` → PASS.
- [ ] **Step 5: Commit** `feat: add generic ARM client`

---

### Task 4: EnsureService (the behavior table)

**Files:**
- Create: `src/InfraIsAlive.Core/Ensure/{EnsureResult,EnsureService}.cs`
- Test: `tests/InfraIsAlive.Tests/{FakeArmClient,EnsureServiceTests}.cs`

**Interfaces:**
- Consumes: `InfraConfig`, `RegisteredResource`, `ResourceTypeProfile` (Task 1); `StateExtractor`, `StateMapper` (Task 2); `IArmClient`, `ArmResponse` (Task 3).
- Produces (namespace `InfraIsAlive.Core.Ensure`):
  - `public enum ResourceState { Online, Starting, Stopped, Failed, Unknown }`
  - `public sealed record EnsureResult(string Name, ResourceState State, bool StartTriggered, string? RawState, string? Message, DateTimeOffset CheckedAt)`
  - `public sealed class EnsureService(InfraConfig config, IArmClient arm, TimeProvider time)` with `Task<IReadOnlyList<EnsureResult>> EnsureAsync(IEnumerable<string> names, CancellationToken ct)` — names trimmed, deduplicated case-insensitively in first-occurrence order, processed in parallel, results returned in that order; `Name` echoes the trimmed requested spelling.
- Test helper: `FakeArmClient : IArmClient` — scripted responses keyed by `(HttpMethod, pathAndQuery)`, optional exception per key, records calls.

Paths: read `"{ResourceId}{StatusPath}?api-version={ApiVersion}"`; start `"{ResourceId}/{StartAction}?api-version={ApiVersion}"` (POST, no body).

Messages (exact strings, for docs consistency):
- not registered: `Resource '{name}' is not registered.`
- read non-success: `Status read failed: ARM returned {code}.`
- read exception: `Status read failed: {exception.Message}`
- raw not found / unmapped: `Raw state '{raw ?? "<none>"}' is not mapped by profile '{type}'.`
- stopping: `Resource is stopping; it will be started by a later call once fully stopped.`
- start conflict: `Start already in progress.`
- start rejected: `Start rejected: ARM returned {code}: {body truncated to 500 chars}` / exception: `Start failed: {exception.Message}`

- [ ] **Step 1: Write failing tests** (one per row of spec 7.1 plus Review Focus items), using `FakeArmClient` and a fixed `TimeProvider`:

```csharp
[Fact] Ensure_Online_NoStart                 // state Online, StartTriggered false, RawState "Ready", no POST recorded
[Fact] Ensure_Starting_NoStart               // Starting, false
[Fact] Ensure_Stopped_TriggersStart          // POST 202 → Starting, true, RawState "Stopped"; exactly one POST to ".../start?api-version=..."
[Fact] Ensure_Stopped_NoStartAction_ReportsStopped   // profile StartAction null → Stopped, false, no POST
[Fact] Ensure_Stopping_ReportsStoppedWithoutStart    // Stopped, false, Message = stopping text, no POST
[Fact] Ensure_StartConflict_ReportsStarting          // POST 409 → Starting, false, Message "Start already in progress."
[Fact] Ensure_StartRejected_ReportsFailed            // POST 400 → Failed, false, Message starts "Start rejected: ARM returned 400"
[Fact] Ensure_MappedFailed_ReportsFailed             // raw "Disabled" → Failed, no POST
[Fact] Ensure_NotRegistered_Unknown                  // Unknown, Message "Resource 'w' is not registered."
[Fact] Ensure_RawStateMissing_Unknown                // fixture without PowerState → Unknown, RawState null, no POST
[Fact] Ensure_Read403_UnknownWithStatusInMessage     // Unknown, Message "Status read failed: ARM returned 403."
[Fact] Ensure_ReadThrows_OnlyThatItemUnknown         // "x" throws HttpRequestException, "z" Online → x Unknown, z Online
[Fact] Ensure_NamesTrimmedCaseInsensitiveAndDeduped  // [" X ","x","Z"] → 2 results, names "X","Z", one GET per resource
[Fact] Ensure_CheckedAtFromTimeProvider
```

- [ ] **Step 2: Run** `dotnet test --filter EnsureServiceTests` → FAIL.
- [ ] **Step 3: Implement** `EnsureService`. Every per-item exception is caught and converted; `EnsureAsync` itself never throws for item problems.
- [ ] **Step 4: Run** `dotnet test --filter EnsureServiceTests` → PASS.
- [ ] **Step 5: Commit** `feat: add ensure service`

---

### Task 5: Function app

**Files:**
- Create: `src/InfraIsAlive.Functions/{InfraIsAlive.Functions.csproj,Program.cs,EnsureFunction.cs,CallerAuthorizer.cs,EnsureRequestParser.cs,host.json,local.settings.example.json}`
- Test: `tests/InfraIsAlive.Tests/FunctionTests.cs`

**Interfaces:**
- Consumes: `ConfigLoader.Load`, `InfraConfig` (Task 1); `ArmHttpClient` (Task 3); `EnsureService`, `EnsureResult`, `ResourceState` (Task 4).
- Produces:
  - `public static bool CallerAuthorizer.IsAllowed(string? principalId, IReadOnlyCollection<string> allowedObjectIds)` — case-insensitive GUID string compare; `false` for null/empty principal or empty list.
  - `public static bool EnsureRequestParser.TryParse(string body, out IReadOnlyList<string> names, out string? error)` — requires JSON object with non-empty `names` array of non-blank strings.
  - `EnsureFunction` — `[Function("ensure")]`, `[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ensure")]`. Order: principal from header `X-MS-CLIENT-PRINCIPAL-ID` (set by Easy Auth; absent → 401) → `IsAllowed` (false → 403) → parse (fail → 400 with `{ "error": "..." }`) → `EnsureService.EnsureAsync` → 200 with the Global Constraints response shape.

Startup (`Program.cs`): `ConfigureFunctionsWebApplication`; load config from `Path.Combine(AppContext.BaseDirectory, "config", "profiles.builtin.json")` and `".../config/infraisalive.json"`; required settings `InfraIsAlive:ArmEndpoint` and `InfraIsAlive:ManagedIdentityClientId` (app settings `InfraIsAlive__ArmEndpoint`, `InfraIsAlive__ManagedIdentityClientId`) — missing → throw naming the setting. Credential: `new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))`. Register `TimeProvider.System`, `HttpClient` via `IHttpClientFactory` with a 30 s timeout. csproj copies `../../config/profiles.builtin.json` and `../../config/infraisalive.json` (the latter `Condition="Exists(...)"`) to `config/` in build and publish output. `host.json`: Application Insights sampling enabled, `logLevel.default: Information`.

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] IsAllowed_ListedPrincipal_True                  // case differs → still true
[Fact] IsAllowed_UnlistedPrincipal_False
[Fact] IsAllowed_EmptyAllowlist_DeniesEveryone
[Fact] IsAllowed_NullPrincipal_False
[Theory] TryParse_Rejects(string body)                 // "", "not json", "{}", "{\"names\":[]}", "{\"names\":[\"  \"]}", "{\"names\":\"x\"}"
[Fact] TryParse_Accepts                                // {"names":["x","z"]} → ["x","z"]
[Fact] Response_SerializesCamelCaseStringEnumsAndOmitsNulls
// serialize one EnsureResult(Online, rawState "Ready", message null) via the same JsonSerializerOptions the function uses
// Assert JSON contains "\"state\":\"Online\"", "\"startTriggered\":false", no "message" property
```

- [ ] **Step 2: Run** `dotnet test --filter FunctionTests` → FAIL.
- [ ] **Step 3: Implement** the four source files and `host.json`; expose the response `JsonSerializerOptions` as `public static readonly JsonSerializerOptions EnsureFunction.ResponseJson`.
- [ ] **Step 4: Run** `dotnet test` (whole suite) → PASS; `dotnet build src/InfraIsAlive.Functions -c Release` → succeeds and output contains `config/profiles.builtin.json`.
- [ ] **Step 5: Commit** `feat: add ensure function`

---

### Task 6: RBAC sync tool

**Files:**
- Create: `tools/InfraIsAlive.RbacSync/{InfraIsAlive.RbacSync.csproj,Program.cs,RbacPlan.cs,RbacSyncRunner.cs}`
- Test: `tests/InfraIsAlive.Tests/RbacSyncTests.cs`

**Interfaces:**
- Consumes: `ConfigLoader.Load`, `InfraConfig` (Task 1); `IArmClient`, `ArmHttpClient` (Task 3); `FakeArmClient` (Task 4).
- Produces:
  - `public sealed record DesiredRbac(Guid RoleDefinitionId, string RoleName, IReadOnlyList<string> Actions, IReadOnlyList<string> Scopes)`
  - `public static DesiredRbac RbacPlan.Build(InfraConfig config, string subscriptionId, string principalId)` — `Actions` = distinct union of `RequiredActions` of the profiles **used by registered resources**, sorted ordinal; `Scopes` = registered `ResourceId`s; throws `InfraConfigException` if any resource's subscription segment ≠ `subscriptionId` (case-insensitive). `RoleDefinitionId` = `RbacPlan.DeterministicGuid($"role|{subscriptionId}|{principalId}")`; `RoleName` = `$"InfraIsAlive Resource Waker ({principalId[..8]})"`.
  - `public static Guid RbacPlan.DeterministicGuid(string seed)` — first 16 bytes of SHA-256 of UTF-8 seed.
  - `public sealed class RbacSyncRunner(IArmClient arm, Func<TimeSpan, CancellationToken, Task> delay)` with `Task RunAsync(DesiredRbac desired, string subscriptionId, string principalId, CancellationToken ct)`.

Runner behavior (API version `2022-04-01` for `Microsoft.Authorization`, passed in from a constant in `RbacPlan` — it is a protocol version, not environment config):
1. PUT `/subscriptions/{sub}/providers/Microsoft.Authorization/roleDefinitions/{RoleDefinitionId}` with `roleName`, `description`, `type: CustomRole`, `permissions: [{ actions }]`, `assignableScopes: ["/subscriptions/{sub}"]`.
2. For each scope: PUT `{scope}/providers/Microsoft.Authorization/roleAssignments/{DeterministicGuid($"assign|{scope.ToLowerInvariant()}|{principalId}")}` with `roleDefinitionId`, `principalId`, `principalType: ServicePrincipal`. A 409 `RoleAssignmentExists` counts as success. A 400 whose body contains `PrincipalNotFound` is retried up to 6 times with `delay(10 s)` (new identity replication), then fails.
3. GET `/subscriptions/{sub}/providers/Microsoft.Authorization/roleAssignments?$filter=principalId eq '{principalId}'&api-version=...`; for each assignment whose `properties.roleDefinitionId` ends with `/{RoleDefinitionId}` and whose `properties.scope` is not in `Scopes` (case-insensitive), DELETE `{assignment.id}?api-version=...`. Assignments of other roles are never touched.
4. Any other non-success → throw with status and body; exit code 1.

CLI: `--subscription-id`, `--principal-id`, `--arm-endpoint`, `--config-dir` (all required; missing → usage message, exit 2). Credential: `ChainedTokenCredential(new AzureDeveloperCliCredential(), new AzureCliCredential())`. Prints one line per created/kept/removed assignment.

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Build_UnionsActionsOfUsedProfilesOnly       // config has vm + pg profiles, only vm used → Actions == vm's 3 actions
[Fact] Build_ResourceInOtherSubscription_Throws
[Fact] DeterministicGuid_IsStable                  // same seed twice equal; different seeds differ
[Fact] Run_PutsRoleDefinitionThenAssignments       // call order: PUT roleDefinitions, PUT per scope, GET list
[Fact] Run_DeletesOnlyStaleAssignmentsOfOwnRole    // list returns: own role @ removed scope, own role @ kept scope, other role @ any scope → exactly one DELETE (the removed scope)
[Fact] Run_RetriesPrincipalNotFoundThenSucceeds    // 400 PrincipalNotFound twice then 201 → succeeds; delay called twice with 10 s
[Fact] Run_AssignmentExists_TreatedAsSuccess       // 409 RoleAssignmentExists → no throw
```

- [ ] **Step 2: Run** `dotnet test --filter RbacSyncTests` → FAIL.
- [ ] **Step 3: Implement** `RbacPlan`, `RbacSyncRunner`, `Program`.
- [ ] **Step 4: Run** `dotnet test` → PASS; `dotnet run --project tools/InfraIsAlive.RbacSync` with no args → exit code 2 and usage text.
- [ ] **Step 5: Commit** `feat: add rbac sync tool`

---

### Task 7: Infrastructure (Bicep + azd)

**Files:**
- Create: `azure.yaml`, `infra/main.bicep`, `infra/main.parameters.json`, `infra/bicepconfig.json`, `infra/app/function.bicep`, `infra/app/entra-app.bicep`, `infra/app/monitoring.bicep`, `scripts/check-config.sh`, `scripts/check-config.ps1`

**Interfaces:**
- Consumes: Function project path (Task 5); RbacSync CLI args (Task 6); `config/infraisalive.json` shape (Task 1).
- Produces (Bicep outputs, which azd exposes as env vars to hooks): `AZURE_SUBSCRIPTION_ID`, `FUNCTION_PRINCIPAL_ID`, `ARM_ENDPOINT`, `FUNCTION_URL`, `API_CLIENT_ID`, `API_AUDIENCE`.

Decisions:
- `main.bicep`: `targetScope = 'subscription'`, creates `rg-${environmentName}`. Parameters: `environmentName`, `location`, `existingApiClientId string = ''`, `enableAppInsights bool = true`, `appInsightsDailyCapGb string = '0.1'` (string, converted to number with `json()`; Bicep has no decimal type). Reads `loadJsonContent('../config/infraisalive.json').AllowedCallerObjectIds`.
- `function.bicep`: Flex Consumption plan (`sku: { name: 'FC1', tier: 'FlexConsumption' }`), Linux function app with `functionAppConfig` → deployment storage blob container authenticated by the user-assigned identity, `runtime: { name: 'dotnet-isolated', version: '10.0' }`, `scaleAndConcurrency.instanceMemoryMB: 512`, no always-ready. Storage account with shared key disabled; the identity gets Storage Blob Data Owner on it. App settings: `AzureWebJobsStorage__accountName`, `AzureWebJobsStorage__credential=managedidentity`, `AzureWebJobsStorage__clientId`, `InfraIsAlive__ArmEndpoint=environment().resourceManager`, `InfraIsAlive__ManagedIdentityClientId`, plus `APPLICATIONINSIGHTS_CONNECTION_STRING` when enabled. `authsettingsV2`: `requireAuthentication: true`, `unauthenticatedClientAction: 'Return401'`, Entra provider with `clientId`, issuer `${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0`, `allowedAudiences: [ 'api://${clientId}', clientId ]`, `defaultAuthorizationPolicy.allowedPrincipals.identities` = the allowlist.
- `entra-app.bicep` (deployed only when `existingApiClientId` is empty): `Microsoft.Graph/applications@v1.0` with `uniqueName: 'infraisalive-${environmentName}'`, `identifierUris: ['api://<appId>']` (set via a second update pattern if the extension needs the appId first — follow the Graph Bicep quickstart for app + identifierUri), `api.requestedAccessTokenVersion: 2`, plus its `servicePrincipals` resource. `bicepconfig.json`: `"extensions": { "microsoftGraphV1": "br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0" }`.
- `monitoring.bicep` (only when enabled): Log Analytics workspace with `workspaceCapping.dailyQuotaGb` from the parameter, workspace-based Application Insights.
- `main.parameters.json` maps `existingApiClientId` → `${EXISTING_API_CLIENT_ID=}`, `enableAppInsights` → `${ENABLE_APP_INSIGHTS=true}`, so adopters set them with `azd env set`.
- `azure.yaml`: service `api` → `project: ./src/InfraIsAlive.Functions`, `language: dotnet`, `host: function`. Hooks: `preprovision` → `scripts/check-config.(sh|ps1)` (fails with "Copy config/infraisalive.example.json to config/infraisalive.json and fill it in" when missing, and when `AllowedCallerObjectIds` is empty); `postprovision` → `dotnet run --project tools/InfraIsAlive.RbacSync -- --subscription-id "$AZURE_SUBSCRIPTION_ID" --principal-id "$FUNCTION_PRINCIPAL_ID" --arm-endpoint "$ARM_ENDPOINT" --config-dir ./config` (posix and windows variants).

- [ ] **Step 1: Write the Bicep files, `azure.yaml`, and both check scripts.**
- [ ] **Step 2: Verify compile** — `cp config/infraisalive.example.json config/infraisalive.json && az bicep build --file infra/main.bicep` → exit 0, no errors (warnings listed in the commit message if any). Delete the copied file afterwards.
- [ ] **Step 3: Verify the guard** — with `config/infraisalive.json` absent, `sh scripts/check-config.sh` → non-zero exit with the copy instruction.
- [ ] **Step 4: Commit** `feat: add bicep infrastructure and azd config`

(Real provisioning happens in Task 9.)

---

### Task 8: Documentation

**Files:**
- Create: `README.md`, `CONTRIBUTING.md`, `docs/deployment.md`, `docs/registering-resources-and-callers.md`, `docs/adding-a-resource-type.md`, `docs/permissions.md`, `docs/troubleshooting.md`

**Interfaces:**
- Consumes: everything above — endpoint contract and messages (Tasks 4–5), config shapes (Task 1), built-in profiles table (Task 2), azd parameters/outputs and hooks (Task 7), RbacSync behavior (Task 6).

Required content (spec §9, plus these pinned points):
- README: problem, the one endpoint with request/response example, state table from spec 7.1, cost profile (Flex free grant 100,000 GB-s and 250,000 executions/month per subscription; storage cents; App Insights optional), the **"every call wakes resources — don't point health probes at it"** warning, quick start linking to deployment.md, MIT badge.
- deployment.md: prerequisites (azd, Azure CLI, .NET 10 SDK; Owner or User Access Administrator on the subscription; Application Developer directory role **or** a pre-created app registration via `azd env set EXISTING_API_CLIENT_ID <id>`; note that **personal Microsoft accounts cannot deploy Graph Bicep resources**, so they must use the pre-created path); copy and fill `config/infraisalive.json`; `azd up`; first-call verification: temporarily add own object ID (`az ad signed-in-user show --query id -o tsv`), `azd provision`, `az account get-access-token --scope "$API_AUDIENCE/.default"`, `curl -X POST "$FUNCTION_URL/api/ensure"`, then remove the ID and re-provision.
- registering-resources-and-callers.md: adding a resource entry; getting a Container App's managed identity object ID; minimal C# caller (`DefaultAzureCredential` / `ManagedIdentityCredential` → token for `api://<clientId>/.default` → POST → poll every 5 s until all `Online` or own timeout); re-run `azd up` after edits.
- adding-a-resource-type.md: find the API version (`az provider show`), inspect a live GET with `az rest`, pick `StatusField` (JSONPath, Newtonsoft syntax, with the VM filter as the worked example), list raw values, find the start action and RBAC actions, add the profile to `config/infraisalive.json`, test locally by adding a fixture + `BuiltinProfile_ExtractsAndMaps` row; contributing it upstream to `profiles.builtin.json`.
- permissions.md: table of `RequiredActions` per built-in profile; how the custom role and per-resource assignments are created and removed; same-subscription limitation for v1.
- troubleshooting.md: 401 (token audience/issuer, Easy Auth), 403 (object ID not on allowlist, or the in-code empty-list deny), `Unknown` messages from Task 4 each with cause and fix, `Failed` from regional capacity (PostgreSQL start not guaranteed), RbacSync `PrincipalNotFound` retries, reading logs in App Insights, upgrading .NET/runtime version in `function.bicep`.
- CONTRIBUTING.md: build/test commands, fixture scrubbing rule, profile contribution checklist.

- [ ] **Step 1: Write all docs.**
- [ ] **Step 2: Verify** — every command, setting name, env var, message string, and file path cited in the docs exists in the code/infra (grep each one); every relative link resolves.
- [ ] **Step 3: Commit** `docs: add readme and guides`

---

### Task 9: Live validation on temporary resources (owner-run)

Deploys and resource creation are run by the owner (CLAUDE.md: deploys are delegated). The executor prepares the exact command list, the owner runs it and pastes back the requested output, the executor applies the results.

**Files:**
- Modify: `config/profiles.builtin.json`, `tests/InfraIsAlive.Tests/Fixtures/*.json`, `tests/InfraIsAlive.Tests/StateEvaluationTests.cs` (add rows for newly observed raw values), docs touched by any correction.

- [ ] **Step 1: Prepare the command list** (one fenced block, numbered): create `rg-infraisalive-validation` in a Flex-supported region; Standard_B1s Linux VM; PostgreSQL Flexible Server Burstable `Standard_B1ms`; Container Apps environment + one hello-world Container App; `az rest` GETs that capture each resource's status JSON in running and stopped states (stop/deallocate commands are for **these temporary resources only**); state the paste-back: the saved JSON files.
- [ ] **Step 2: Apply recordings** — scrub IDs/names to placeholders, replace fixtures, correct `ApiVersion`/`StatusField`/`StateMap` in `profiles.builtin.json` to what was observed; `dotnet test` → PASS.
- [ ] **Step 3: Prepare the deploy list** — fill `config/infraisalive.json` with the three temp resources and the owner's object ID; `azd up` (fallback: pre-created app registration if the Graph step fails, e.g. personal Microsoft account); paste-back: azd outputs and any error lines.
- [ ] **Step 4: End-to-end checks the owner runs and pastes back:** with all three stopped, `POST /api/ensure {"names":["vm","pg","app","nope"]}` → three `Starting` with `startTriggered: true` and `nope` `Unknown`; repeat every 15 s until all three `Online` (record elapsed times for the README); call with no token → 401; with a token from an identity not on the list → 403; remove `app` from config + `azd up` → its role assignment is gone (`az role assignment list --assignee <principal> --all`).
- [ ] **Step 5: Fix** whatever the checks expose (each fix with a test where code changed); `dotnet test` → PASS.
- [ ] **Step 6: Teardown list** — `azd down --purge` and `az group delete -n rg-infraisalive-validation`; owner confirms both gone.
- [ ] **Step 7: Commit** `test: record live ARM samples and correct built-in profiles`
