namespace InfraIsAlive.Functions;

public static class CallerAuthorizer
{
    /// <summary>
    /// The caller's Entra object ID must be on the allowlist. An empty allowlist denies everyone: Easy Auth
    /// alone would admit any principal in the tenant when its allowed identities list is empty.
    /// </summary>
    public static bool IsAllowed(string? principalId, IReadOnlyCollection<string> allowedObjectIds) =>
        !string.IsNullOrWhiteSpace(principalId)
        && allowedObjectIds.Any(id => string.Equals(id.Trim(), principalId.Trim(), StringComparison.OrdinalIgnoreCase));
}
