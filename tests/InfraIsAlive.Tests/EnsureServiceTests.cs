using InfraIsAlive.Core.Arm;
using InfraIsAlive.Core.Configuration;
using InfraIsAlive.Core.Ensure;
using Microsoft.Extensions.Time.Testing;

namespace InfraIsAlive.Tests;

public sealed class EnsureServiceTests
{
    private const string PgId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.DBforPostgreSQL/flexibleServers/z";
    private const string VmId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/x";
    private const string RoId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Cache/redis/r";

    private const string PgRead = PgId + "?api-version=2024-08-01";
    private const string PgStart = PgId + "/start?api-version=2024-08-01";
    private const string VmRead = VmId + "/instanceView?api-version=2024-07-01";
    private const string RoRead = RoId + "?api-version=2024-01-01";

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);

    private readonly FakeArmClient _arm = new();

    private EnsureService Service()
    {
        var builtin = StateEvaluationTests.BuiltinOnly();
        var types = new Dictionary<string, ResourceTypeProfile>(builtin.ResourceTypes, StringComparer.OrdinalIgnoreCase)
        {
            ["readonly"] = builtin.ResourceTypes["postgres-flexible"] with { ApiVersion = "2024-01-01", StartAction = null },
        };
        var resources = new Dictionary<string, RegisteredResource>(StringComparer.OrdinalIgnoreCase)
        {
            ["z"] = new("postgres-flexible", PgId),
            ["x"] = new("vm", VmId),
            ["r"] = new("readonly", RoId),
        };
        var time = new FakeTimeProvider(Now);
        return new EnsureService(new InfraConfig([], types, resources), _arm, time);
    }

    private static ArmResponse Pg(string state) => new(200, $$"""{ "properties": { "state": "{{state}}" } }""");

    private async Task<EnsureResult> One(string name) => Assert.Single(await Service().EnsureAsync([name], CancellationToken.None));

    [Fact]
    public async Task Ensure_Online_NoStart()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Ready"));

        var r = await One("z");

        Assert.Equal(new EnsureResult("z", ResourceState.Online, false, "Ready", null, Now), r);
        Assert.Equal(0, _arm.Count(HttpMethod.Post));
    }

    [Fact]
    public async Task Ensure_Starting_NoStart()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Starting"));

        var r = await One("z");

        Assert.Equal((ResourceState.Starting, false), (r.State, r.StartTriggered));
        Assert.Equal(0, _arm.Count(HttpMethod.Post));
    }

    [Fact]
    public async Task Ensure_Stopped_TriggersStart()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Stopped")).On(HttpMethod.Post, PgStart, new ArmResponse(202, ""));

        var r = await One("z");

        Assert.Equal((ResourceState.Starting, true, "Stopped"), (r.State, r.StartTriggered, r.RawState));
        var post = Assert.Single(_arm.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal(PgStart, post.Path);
        Assert.Null(post.Body);
    }

    [Fact]
    public async Task Ensure_Stopped_NoStartAction_ReportsStopped()
    {
        _arm.On(HttpMethod.Get, RoRead, Pg("Stopped"));

        var r = await One("r");

        Assert.Equal((ResourceState.Stopped, false), (r.State, r.StartTriggered));
        Assert.Equal(0, _arm.Count(HttpMethod.Post));
    }

    [Fact]
    public async Task Ensure_Stopping_ReportsStoppedWithoutStart()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Stopping"));

        var r = await One("z");

        Assert.Equal((ResourceState.Stopped, false), (r.State, r.StartTriggered));
        Assert.Equal("Resource is stopping; it will be started by a later call once fully stopped.", r.Message);
        Assert.Equal(0, _arm.Count(HttpMethod.Post));
    }

    [Fact]
    public async Task Ensure_StartConflict_ReportsStarting()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Stopped")).On(HttpMethod.Post, PgStart, new ArmResponse(409, "{}"));

        var r = await One("z");

        Assert.Equal((ResourceState.Starting, false, "Start already in progress."), (r.State, r.StartTriggered, r.Message));
    }

    [Fact]
    public async Task Ensure_StartRejected_ReportsFailed()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Stopped")).On(HttpMethod.Post, PgStart, new ArmResponse(400, "capacity"));

        var r = await One("z");

        Assert.Equal((ResourceState.Failed, false), (r.State, r.StartTriggered));
        Assert.StartsWith("Start rejected: ARM returned 400", r.Message);
        Assert.Contains("capacity", r.Message);
    }

    [Fact]
    public async Task Ensure_StartThrows_ReportsFailed()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Stopped")).Throws(HttpMethod.Post, PgStart, new HttpRequestException("boom"));

        var r = await One("z");

        Assert.Equal((ResourceState.Failed, false, "Start failed: boom"), (r.State, r.StartTriggered, r.Message));
    }

    [Fact]
    public async Task Ensure_MappedFailed_ReportsFailed()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Disabled"));

        var r = await One("z");

        Assert.Equal((ResourceState.Failed, false, "Disabled"), (r.State, r.StartTriggered, r.RawState));
        Assert.Equal(0, _arm.Count(HttpMethod.Post));
    }

    [Fact]
    public async Task Ensure_NotRegistered_Unknown()
    {
        var r = await One("w");

        Assert.Equal(new EnsureResult("w", ResourceState.Unknown, false, null, "Resource 'w' is not registered.", Now), r);
        Assert.Empty(_arm.Calls);
    }

    [Fact]
    public async Task Ensure_RawStateMissing_Unknown()
    {
        _arm.On(HttpMethod.Get, VmRead, new ArmResponse(200, StateEvaluationTests.Fixture("vm-instanceview-no-powerstate.json")));

        var r = await One("x");

        Assert.Equal((ResourceState.Unknown, (string?)null), (r.State, r.RawState));
        Assert.Equal("Raw state '<none>' is not mapped by profile 'vm'.", r.Message);
        Assert.Equal(0, _arm.Count(HttpMethod.Post));
    }

    [Fact]
    public async Task Ensure_UnmappedRawValue_Unknown()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Hibernating"));

        var r = await One("z");

        Assert.Equal((ResourceState.Unknown, "Hibernating", "Raw state 'Hibernating' is not mapped by profile 'postgres-flexible'."), (r.State, r.RawState, r.Message));
    }

    [Fact]
    public async Task Ensure_Read403_UnknownWithStatusInMessage()
    {
        _arm.On(HttpMethod.Get, PgRead, new ArmResponse(403, "forbidden"));

        var r = await One("z");

        Assert.Equal((ResourceState.Unknown, "Status read failed: ARM returned 403."), (r.State, r.Message));
    }

    [Fact]
    public async Task Ensure_ReadThrows_OnlyThatItemUnknown()
    {
        _arm.Throws(HttpMethod.Get, VmRead, new HttpRequestException("network down")).On(HttpMethod.Get, PgRead, Pg("Ready"));

        var results = await Service().EnsureAsync(["x", "z"], CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal((ResourceState.Unknown, "Status read failed: network down"), (results[0].State, results[0].Message));
        Assert.Equal(ResourceState.Online, results[1].State);
    }

    [Fact]
    public async Task Ensure_NamesTrimmedCaseInsensitiveAndDeduped()
    {
        _arm.On(HttpMethod.Get, VmRead, new ArmResponse(200, StateEvaluationTests.Fixture("vm-instanceview-running.json")))
            .On(HttpMethod.Get, PgRead, Pg("Ready"));

        var results = await Service().EnsureAsync([" X ", "x", "Z"], CancellationToken.None);

        Assert.Equal(["X", "Z"], results.Select(r => r.Name));
        Assert.Equal(1, _arm.Calls.Count(c => c.Path == VmRead));
        Assert.Equal(1, _arm.Calls.Count(c => c.Path == PgRead));
    }

    [Fact]
    public async Task Ensure_CheckedAtFromTimeProvider()
    {
        _arm.On(HttpMethod.Get, PgRead, Pg("Ready"));

        Assert.Equal(Now, (await One("z")).CheckedAt);
    }
}
