using InfraIsAlive.Core.Arm;
using InfraIsAlive.Core.Configuration;
using InfraIsAlive.Core.Evaluation;

namespace InfraIsAlive.Core.Ensure;

/// <summary>
/// For each requested name: reads the live state from ARM and starts the resource when it is stopped.
/// A problem with one resource is reported in its own result and never fails the whole call.
/// </summary>
public sealed class EnsureService(InfraConfig config, IArmClient arm, TimeProvider time)
{
    private const int MaxBodyInMessage = 500;

    public async Task<IReadOnlyList<EnsureResult>> EnsureAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var distinct = names
            .Select(n => n.Trim())
            .DistinctBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await Task.WhenAll(distinct.Select(n => EnsureOneAsync(n, ct)));
    }

    private async Task<EnsureResult> EnsureOneAsync(string name, CancellationToken ct)
    {
        EnsureResult Result(ResourceState state, bool triggered, string? raw, string? message) =>
            new(name, state, triggered, raw, message, time.GetUtcNow());

        if (!config.Resources.TryGetValue(name, out var resource))
            return Result(ResourceState.Unknown, false, null, $"Resource '{name}' is not registered.");

        var profile = config.ResourceTypes[resource.Type];

        string? raw;
        try
        {
            var read = await arm.SendAsync(HttpMethod.Get, $"{resource.ResourceId}{profile.StatusPath}?api-version={profile.ApiVersion}", null, ct);
            if (!read.IsSuccess)
                return Result(ResourceState.Unknown, false, null, $"Status read failed: ARM returned {read.StatusCode}.");
            raw = StateExtractor.Extract(read.Body, profile.StatusField);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result(ResourceState.Unknown, false, null, $"Status read failed: {ex.Message}");
        }

        switch (StateMapper.Map(profile, raw))
        {
            case MappedState.Online:
                return Result(ResourceState.Online, false, raw, null);
            case MappedState.Starting:
                return Result(ResourceState.Starting, false, raw, null);
            case MappedState.Failed:
                return Result(ResourceState.Failed, false, raw, null);
            case MappedState.Stopping:
                return Result(ResourceState.Stopped, false, raw, "Resource is stopping; it will be started by a later call once fully stopped.");
            case MappedState.Stopped when profile.StartAction is null:
                return Result(ResourceState.Stopped, false, raw, null);
            case MappedState.Stopped:
                return await StartAsync(resource, profile, raw, Result, ct);
            default:
                return Result(ResourceState.Unknown, false, raw, $"Raw state '{raw ?? "<none>"}' is not mapped by profile '{resource.Type}'.");
        }
    }

    private async Task<EnsureResult> StartAsync(
        RegisteredResource resource,
        ResourceTypeProfile profile,
        string? raw,
        Func<ResourceState, bool, string?, string?, EnsureResult> result,
        CancellationToken ct)
    {
        try
        {
            var start = await arm.SendAsync(HttpMethod.Post, $"{resource.ResourceId}/{profile.StartAction}?api-version={profile.ApiVersion}", null, ct);
            if (start.IsSuccess) return result(ResourceState.Starting, true, raw, null);
            if (start.StatusCode == 409) return result(ResourceState.Starting, false, raw, "Start already in progress.");

            var body = start.Body.Length > MaxBodyInMessage ? start.Body[..MaxBodyInMessage] : start.Body;
            return result(ResourceState.Failed, false, raw, $"Start rejected: ARM returned {start.StatusCode}: {body}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return result(ResourceState.Failed, false, raw, $"Start failed: {ex.Message}");
        }
    }
}
