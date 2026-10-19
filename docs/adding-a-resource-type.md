# Adding a new resource type

InfraIsAlive does not know specific Azure services. It knows **profiles**: small JSON descriptions of how to read a
resource's state through the generic Azure Resource Manager (ARM) REST API, and how to start it. Adding a type
means writing a profile. No code change is needed.

## Anatomy of a profile

```json
"ResourceTypes": {
  "app-service": {
    "ApiVersion": "2024-04-01",
    "StatusPath": "",
    "StatusField": "$.properties.state",
    "StartAction": "start",
    "StateMap": { "Running": "Online", "Stopped": "Stopped" },
    "RequiredActions": [
      "Microsoft.Web/sites/read",
      "Microsoft.Web/sites/start/action"
    ]
  }
}
```

| Field | Meaning |
|---|---|
| `ApiVersion` | ARM API version of the resource provider. |
| `StatusPath` | Suffix added to the resource ID for the status read. Usually empty; `/instanceView` for VMs. |
| `StatusField` | [JSONPath](https://www.newtonsoft.com/json/help/html/QueryJsonSelectToken.htm) (Newtonsoft syntax, filters supported) that selects the raw state. |
| `StartAction` | Action POSTed as `{resourceId}/{StartAction}?api-version=...`. Omit it for a type that can't be started; InfraIsAlive then only reports `Stopped`. |
| `StateMap` | Raw value → `Online`, `Starting`, `Stopped`, `Stopping` or `Failed`. Matching ignores case. A raw value that isn't listed is reported as `Unknown`. |
| `RequiredActions` | RBAC actions the Function needs for this type. `RbacSync` builds the custom role from them. |

The status read is `GET {resourceId}{StatusPath}?api-version={ApiVersion}`.

## Step by step

Using an App Service as the example.

**1. Find an API version.**

```sh
az provider show -n Microsoft.Web --query "resourceTypes[?resourceType=='sites'].apiVersions[0]" -o tsv
```

Prefer a recent non-preview version.

**2. Look at a live response.**

```sh
az rest --method get --url "<resourceId>?api-version=<version>"
```

Find the field that changes between running and stopped (`properties.state` here). Some services keep their power
state in a sub-resource. The VM profile is the worked example: its state sits in the `statuses` array of
`/instanceView`, next to unrelated entries, so the profile uses a filter:

```
$.statuses[?(@.code =~ /^PowerState\/.*$/)].code
```

In JSON, escape the backslash: `"$.statuses[?(@.code =~ /^PowerState\\/.*$/)].code"`.

**3. List the raw values.** Stop and start a test resource and read the field in each state. The provider's REST
reference also lists the enum. Map every value you expect, including transitions such as `Stopping`.

**4. Find the start action and RBAC actions.**

```sh
az provider operation show --namespace Microsoft.Web \
  --query "resourceTypes[?name=='sites'].operations[].name" -o tsv | grep -Ei '/read$|/start/action$'
```

Use only `read` actions plus the start action. Never add write or delete actions.

**5. Add the profile** under `ResourceTypes` in `config/infraisalive.json`, register a resource with that `Type`,
and run `azd up`.

**6. Check it.** Call `/api/ensure` for the resource. `rawState` shows what your `StatusField` selected. If the
result is `Unknown` with `Raw state '...' is not mapped`, add that value to `StateMap`; if it says `<none>`, your
`StatusField` matched nothing.

An adopter profile with the same key as a built-in one **replaces it entirely**, so you can also fix a built-in
profile locally while waiting for an upstream fix.

## Contributing a profile upstream

Built-in profiles live in `config/profiles.builtin.json`. To add one:

1. Add the profile there.
2. Record real responses (running and stopped) as fixtures in `tests/InfraIsAlive.Tests/Fixtures/`, **scrubbed**
   of subscription IDs, tenant IDs and resource names (use `00000000-0000-0000-0000-000000000000` and
   `rg-example`).
3. Add `[InlineData]` rows to `BuiltinProfile_ExtractsAndMaps` in `StateEvaluationTests.cs`.
4. Add the profile's actions to [permissions.md](permissions.md).

See [CONTRIBUTING.md](../CONTRIBUTING.md).
