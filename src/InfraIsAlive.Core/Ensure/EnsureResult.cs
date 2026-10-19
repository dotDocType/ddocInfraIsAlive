namespace InfraIsAlive.Core.Ensure;

public enum ResourceState
{
    Online,
    Starting,
    Stopped,
    Failed,
    Unknown,
}

/// <param name="StartTriggered">True only when Azure accepted a start action during this call.</param>
public sealed record EnsureResult(
    string Name,
    ResourceState State,
    bool StartTriggered,
    string? RawState,
    string? Message,
    DateTimeOffset CheckedAt);
