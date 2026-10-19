using System.Security.Cryptography;
using System.Text;
using InfraIsAlive.Core.Configuration;

namespace InfraIsAlive.RbacSync;

/// <param name="Actions">Distinct union of the RequiredActions of the profiles used by registered resources.</param>
/// <param name="Scopes">One role assignment per registered resource ID.</param>
public sealed record DesiredRbac(Guid RoleDefinitionId, string RoleName, IReadOnlyList<string> Actions, IReadOnlyList<string> Scopes);

public static class RbacPlan
{
    /// <summary>Microsoft.Authorization REST API version (a protocol version, not environment configuration).</summary>
    public const string AuthorizationApiVersion = "2022-04-01";

    public static DesiredRbac Build(InfraConfig config, string subscriptionId, string principalId)
    {
        var errors = config.Resources
            .Where(r => !string.Equals(r.Value.ResourceId.Split('/')[2], subscriptionId, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"Resource '{r.Key}' is in subscription '{r.Value.ResourceId.Split('/')[2]}', but only the deployment subscription '{subscriptionId}' is supported.")
            .ToList();
        if (errors.Count > 0) throw new InfraConfigException(errors);

        var actions = config.Resources.Values
            .Select(r => r.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(t => config.ResourceTypes[t].RequiredActions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new DesiredRbac(
            DeterministicGuid($"role|{subscriptionId}|{principalId}"),
            $"InfraIsAlive Resource Waker ({principalId[..8]})",
            actions,
            config.Resources.Values.Select(r => r.ResourceId).ToList());
    }

    /// <summary>Stable GUID from a seed, so re-running the sync addresses the same role definition and assignments.</summary>
    public static Guid DeterministicGuid(string seed) => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
}
