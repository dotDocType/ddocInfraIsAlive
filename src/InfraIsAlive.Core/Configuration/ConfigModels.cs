namespace InfraIsAlive.Core.Configuration;

/// <summary>Target of a profile's state map. <see cref="Stopping"/> is internal and is reported as Stopped.</summary>
public enum MappedState
{
    Online,
    Starting,
    Stopped,
    Stopping,
    Failed,
}

/// <summary>Describes how to read and start one kind of Azure resource through the generic ARM REST API.</summary>
public sealed record ResourceTypeProfile(
    string ApiVersion,
    string StatusPath,
    string StatusField,
    string? StartAction,
    IReadOnlyDictionary<string, MappedState> StateMap,
    IReadOnlyList<string> RequiredActions);

public sealed record RegisteredResource(string Type, string ResourceId);

public sealed record InfraConfig(
    IReadOnlyList<string> AllowedCallerObjectIds,
    IReadOnlyDictionary<string, ResourceTypeProfile> ResourceTypes,
    IReadOnlyDictionary<string, RegisteredResource> Resources);
