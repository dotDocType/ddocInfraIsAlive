# Registering resources and allowing callers

Everything here lives in `config/infraisalive.json`. Re-run `azd up` after every edit: the config file ships
inside the Function package, and the permissions and the Easy Auth allowlist are re-applied from it.

## Register a resource

Add an entry under `Resources`. The key is the name your products send in `names`; matching ignores case and
surrounding spaces.

```json
"Resources": {
  "site-builder-db": {
    "Type": "postgres-flexible",
    "ResourceId": "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.DBforPostgreSQL/flexibleServers/<server>"
  }
}
```

- `Type` must be a built-in profile (`vm`, `postgres-flexible`, `container-app`) or one you defined under
  `ResourceTypes`.
- `ResourceId` must be the full ARM ID, in the same subscription as the deployment.

On the next `azd up`, `RbacSync` grants the Function's identity the read and start actions for that resource, and
nothing more.

**Unregistering** a resource is the reverse: delete the entry and run `azd up`. Its role assignment is removed.

## Allow a caller

Add the caller's **Entra object ID** (also called the principal ID) to `AllowedCallerObjectIds`.

Container App with a system-assigned identity:

```sh
az containerapp identity assign -n <app> -g <rg> --system-assigned   # if not enabled yet
az containerapp identity show -n <app> -g <rg> --query principalId -o tsv
```

User-assigned identity:

```sh
az identity show -n <identity> -g <rg> --query principalId -o tsv
```

Other Azure hosts (App Service, Functions, VMs, AKS workload identity) expose the same `principalId` for their
managed identity.

The allowlist is enforced twice: Easy Auth rejects tokens from principals that aren't listed, and the Function
itself denies any principal that isn't listed. **An empty list denies everyone.**

**Limit:** Easy Auth accepts at most **500 characters in total** across the list, which is about **13 object
IDs**. The config guard stops `azd up` if you go over. If you have more callers, give them a shared user-assigned
managed identity.

## Calling from a product (C#)

The caller needs two values from the deployment, kept in its own configuration:

- `InfraIsAlive:Url`: the value of `azd env get-value FUNCTION_URL`
- `InfraIsAlive:Audience`: the value of `azd env get-value API_AUDIENCE` (the app registration's client ID)

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Identity;

public sealed record EnsureItem(string Name, string State, bool StartTriggered, string? RawState, string? Message);
public sealed record EnsureResponse(List<EnsureItem> Results);

public sealed class InfraIsAliveClient(HttpClient http, IConfiguration config)
{
    // Use ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(...)) for a user-assigned identity.
    private readonly TokenCredential _credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);

    public async Task<EnsureResponse> EnsureAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var audience = config["InfraIsAlive:Audience"] ?? throw new InvalidOperationException("Missing InfraIsAlive:Audience");
        var url = config["InfraIsAlive:Url"] ?? throw new InvalidOperationException("Missing InfraIsAlive:Url");

        var token = await _credential.GetTokenAsync(new TokenRequestContext([$"{audience}/.default"]), ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/ensure")
        {
            Content = JsonContent.Create(new { names }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnsureResponse>(ct))!;
    }

    /// <summary>
    /// Polls until every resource is Online, or gives up at the caller's own deadline. Unknown and Failed are
    /// often transient (a network blip, a throttled read), so keep polling; only an unregistered name is final.
    /// </summary>
    public async Task<bool> WaitUntilOnlineAsync(string[] names, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await EnsureAsync(names, ct);
            if (result.Results.All(r => r.State == "Online")) return true;
            if (result.Results.Any(r => r.Message?.EndsWith("is not registered.") == true)) return false; // config error
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return false; // log the last results' Message for whoever investigates
    }
}
```

The product decides what the user sees while it waits, and how long it is willing to wait. As a reference, in a
validation run from fully stopped, a VM (2 vCPU) and a Container App were `Online` in under a minute, and a
Burstable PostgreSQL Flexible Server in about three minutes.

A single call can take up to about 30 seconds when a resource provider is slow to answer: each ARM request has a
30-second timeout, and that resource then comes back as `Unknown` with a `Status read failed` message. Keep the
caller's HTTP timeout above that (the `HttpClient` default of 100 seconds is fine).
