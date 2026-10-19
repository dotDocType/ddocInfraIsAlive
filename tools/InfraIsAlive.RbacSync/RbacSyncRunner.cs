using System.Text.Json;
using InfraIsAlive.Core.Arm;

namespace InfraIsAlive.RbacSync;

/// <summary>
/// Creates or updates the custom role, ensures one assignment per desired scope, and deletes this role's
/// assignments on scopes no longer configured. Assignments of other roles are never touched.
/// </summary>
public sealed class RbacSyncRunner(IArmClient arm, Func<TimeSpan, CancellationToken, Task> delay)
{
    private const int ReplicationRetries = 6;
    private static readonly TimeSpan ReplicationDelay = TimeSpan.FromSeconds(10);
    private static readonly string[] ReplicationErrorCodes = ["PrincipalNotFound", "RoleDefinitionDoesNotExist"];
    private const string Api = "api-version=" + RbacPlan.AuthorizationApiVersion;

    public async Task RunAsync(DesiredRbac desired, string subscriptionId, string principalId, CancellationToken ct)
    {
        var subscriptionScope = $"/subscriptions/{subscriptionId}";
        var roleDefinitionId = $"{subscriptionScope}/providers/Microsoft.Authorization/roleDefinitions/{desired.RoleDefinitionId}";

        var roleBody = JsonSerializer.Serialize(new
        {
            properties = new
            {
                roleName = desired.RoleName,
                description = "Lets the InfraIsAlive function read and start its registered resources. Managed by InfraIsAlive.RbacSync.",
                type = "CustomRole",
                permissions = new[] { new { actions = desired.Actions, notActions = Array.Empty<string>() } },
                assignableScopes = new[] { subscriptionScope },
            },
        });
        // With nothing registered there is nothing to grant; skip the role update (ARM may reject an empty
        // actions list) but still remove assignments left over from resources that were unregistered.
        if (desired.Actions.Count > 0)
        {
            Ensure(await arm.SendAsync(HttpMethod.Put, $"{roleDefinitionId}?{Api}", roleBody, ct), $"create/update role '{desired.RoleName}'");
            Console.WriteLine($"Role '{desired.RoleName}' ({desired.RoleDefinitionId}): {desired.Actions.Count} actions.");
        }

        await Task.WhenAll(desired.Scopes.Select(scope => AssignAsync(scope, roleDefinitionId, principalId, ct)));

        var desiredScopes = new HashSet<string>(desired.Scopes, StringComparer.OrdinalIgnoreCase);
        var filter = Uri.EscapeDataString($"principalId eq '{principalId}'");
        var list = await arm.SendAsync(HttpMethod.Get, $"{subscriptionScope}/providers/Microsoft.Authorization/roleAssignments?$filter={filter}&{Api}", null, ct);
        Ensure(list, "list role assignments");

        using var doc = JsonDocument.Parse(list.Body);
        foreach (var assignment in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var properties = assignment.GetProperty("properties");
            var role = properties.GetProperty("roleDefinitionId").GetString() ?? "";
            var scope = properties.GetProperty("scope").GetString() ?? "";
            if (!role.EndsWith($"/{desired.RoleDefinitionId}", StringComparison.OrdinalIgnoreCase) || desiredScopes.Contains(scope)) continue;

            var id = assignment.GetProperty("id").GetString();
            Ensure(await arm.SendAsync(HttpMethod.Delete, $"{id}?{Api}", null, ct), $"remove assignment on {scope}");
            Console.WriteLine($"Removed: {scope}");
        }
    }

    private async Task AssignAsync(string scope, string roleDefinitionId, string principalId, CancellationToken ct)
    {
        var assignmentId = RbacPlan.DeterministicGuid($"assign|{scope.ToLowerInvariant()}|{principalId}");
        var body = JsonSerializer.Serialize(new
        {
            properties = new { roleDefinitionId, principalId, principalType = "ServicePrincipal" },
        });

        for (var attempt = 0; ; attempt++)
        {
            var response = await arm.SendAsync(HttpMethod.Put, $"{scope}/providers/Microsoft.Authorization/roleAssignments/{assignmentId}?{Api}", body, ct);
            if (response.IsSuccess)
            {
                Console.WriteLine($"Assigned: {scope}");
                return;
            }

            if (response.StatusCode == 409 && response.Body.Contains("RoleAssignmentExists", StringComparison.Ordinal))
            {
                Console.WriteLine($"Kept: {scope}");
                return;
            }

            var replicating = response.StatusCode == 400 && ReplicationErrorCodes.Any(c => response.Body.Contains(c, StringComparison.Ordinal));
            if (!replicating || attempt == ReplicationRetries) Ensure(response, $"assign role on {scope}");

            Console.WriteLine($"Waiting for Entra/ARM replication before assigning on {scope} (attempt {attempt + 1}/{ReplicationRetries})...");
            await delay(ReplicationDelay, ct);
        }
    }

    private static void Ensure(ArmResponse response, string what)
    {
        if (!response.IsSuccess)
            throw new InvalidOperationException($"Failed to {what}: ARM returned {response.StatusCode}: {response.Body}");
    }
}
