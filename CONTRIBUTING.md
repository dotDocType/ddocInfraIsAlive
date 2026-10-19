# Contributing

Thanks for helping. Issues and pull requests are welcome.

## Build and test

```sh
dotnet build
dotnet test
```

You need the .NET 10 SDK (pinned in `global.json`). To compile the infrastructure:

```sh
cp config/infraisalive.example.json config/infraisalive.json   # Bicep reads it at compile time
az bicep build --file infra/main.bicep
rm config/infraisalive.json
```

## Layout

| Path | What |
|---|---|
| `src/InfraIsAlive.Core` | Config model and loader, profile evaluation, generic ARM client, `EnsureService` |
| `src/InfraIsAlive.Functions` | The HTTP function, request parsing, caller allowlist check |
| `tools/InfraIsAlive.RbacSync` | Custom role and role-assignment sync, run by the azd postprovision hook |
| `config/profiles.builtin.json` | Built-in resource-type profiles |
| `infra/` | Bicep and azd |
| `tests/InfraIsAlive.Tests` | xUnit tests and recorded ARM fixtures |

## Rules

- **Tests first.** Every behavior change comes with a test that failed before the change.
- **No environment-coupled values in code.** Endpoints, IDs, API versions of target resources and callers all come
  from configuration.
- **Scrub fixtures.** Recorded ARM responses must not contain real subscription IDs, tenant IDs, object IDs or
  resource names. Use `00000000-0000-0000-0000-000000000000`, `rg-example`, `vm-example` and similar.
- **Never add write or delete RBAC actions** to a profile. InfraIsAlive only reads and starts.
- Everything written to files is in English.

## Contributing a resource-type profile

1. Add it to `config/profiles.builtin.json` (see [docs/adding-a-resource-type.md](docs/adding-a-resource-type.md)).
2. Add scrubbed fixtures for at least the running and stopped states.
3. Add `[InlineData]` rows to `BuiltinProfile_ExtractsAndMaps`.
4. Document its actions in [docs/permissions.md](docs/permissions.md).
5. Say in the pull request which real resource you validated it against.
