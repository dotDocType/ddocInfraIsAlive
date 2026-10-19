using InfraIsAlive.Core.Configuration;

namespace InfraIsAlive.Core.Evaluation;

public static class StateMapper
{
    /// <summary>Returns null when the raw value is missing or not in the profile's state map.</summary>
    public static MappedState? Map(ResourceTypeProfile profile, string? rawState) =>
        rawState is not null && profile.StateMap.TryGetValue(rawState, out var mapped) ? mapped : null;
}
