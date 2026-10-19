using System.Collections.Concurrent;
using InfraIsAlive.Core.Arm;

namespace InfraIsAlive.Tests;

/// <summary>Scripted ARM client: responses or exceptions keyed by (method, pathAndQuery); records every call.</summary>
internal sealed class FakeArmClient : IArmClient
{
    private readonly ConcurrentDictionary<(HttpMethod, string), Queue<Func<ArmResponse>>> _script = new();

    public ConcurrentQueue<(HttpMethod Method, string Path, string? Body)> Calls { get; } = new();

    /// <summary>Queues responses for a call; the last one repeats once the queue is drained.</summary>
    public FakeArmClient On(HttpMethod method, string pathAndQuery, params ArmResponse[] responses)
    {
        var queue = _script.GetOrAdd((method, pathAndQuery), _ => new Queue<Func<ArmResponse>>());
        foreach (var r in responses) queue.Enqueue(() => r);
        return this;
    }

    public FakeArmClient Throws(HttpMethod method, string pathAndQuery, Exception ex)
    {
        _script.GetOrAdd((method, pathAndQuery), _ => new Queue<Func<ArmResponse>>()).Enqueue(() => throw ex);
        return this;
    }

    public Task<ArmResponse> SendAsync(HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken ct)
    {
        Calls.Enqueue((method, pathAndQuery, jsonBody));
        if (!_script.TryGetValue((method, pathAndQuery), out var queue) || queue.Count == 0)
            throw new InvalidOperationException($"Unscripted ARM call: {method} {pathAndQuery}");

        lock (queue)
        {
            var next = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Task.FromResult(next());
        }
    }

    public int Count(HttpMethod method) => Calls.Count(c => c.Method == method);
}
