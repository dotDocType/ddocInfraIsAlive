using InfraIsAlive.Core.Arm;
using InfraIsAlive.Core.Configuration;
using InfraIsAlive.RbacSync;

namespace InfraIsAlive.Tests;

public sealed class RbacSyncTests
{
    private const string Sub = "sub-1";
    private const string Principal = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string VmId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-1";
    private const string Vm2Id = "/subscriptions/sub-1/resourceGroups/rg-other/providers/Microsoft.Compute/virtualMachines/vm-2";
    private const string Api = "?api-version=" + RbacPlan.AuthorizationApiVersion;

    private static InfraConfig Config(params (string Name, string Type, string Id)[] resources)
    {
        var builtin = StateEvaluationTests.BuiltinOnly();
        return builtin with
        {
            Resources = resources.ToDictionary(r => r.Name, r => new RegisteredResource(r.Type, r.Id), StringComparer.OrdinalIgnoreCase),
        };
    }

    private static DesiredRbac Desired(params string[] scopes) =>
        RbacPlan.Build(Config(scopes.Select((s, i) => ($"r{i}", "vm", s)).ToArray()), Sub, Principal);

    private static string RoleDefPath(DesiredRbac d) => $"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleDefinitions/{d.RoleDefinitionId}{Api}";

    private static string AssignPath(string scope) =>
        $"{scope}/providers/Microsoft.Authorization/roleAssignments/{RbacPlan.DeterministicGuid($"assign|{scope.ToLowerInvariant()}|{Principal}")}{Api}";

    private static readonly string ListPath =
        $"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleAssignments?$filter={Uri.EscapeDataString($"principalId eq '{Principal}'")}&api-version={RbacPlan.AuthorizationApiVersion}";

    private static ArmResponse List(params (string Id, string RoleDefId, string Scope)[] items) =>
        new(200, System.Text.Json.JsonSerializer.Serialize(new
        {
            value = items.Select(i => new { id = i.Id, properties = new { roleDefinitionId = i.RoleDefId, scope = i.Scope, principalId = Principal } }),
        }));

    private readonly List<TimeSpan> _delays = [];

    private RbacSyncRunner Runner(FakeArmClient arm) => new(arm, (d, _) => { _delays.Add(d); return Task.CompletedTask; });

    [Fact]
    public void Build_UnionsActionsOfUsedProfilesOnly()
    {
        var desired = RbacPlan.Build(Config(("x", "vm", VmId)), Sub, Principal);

        Assert.Equal(
            ["Microsoft.Compute/virtualMachines/instanceView/read", "Microsoft.Compute/virtualMachines/read", "Microsoft.Compute/virtualMachines/start/action"],
            desired.Actions);
        Assert.Equal([VmId], desired.Scopes);
        Assert.Equal($"InfraIsAlive Resource Waker ({Principal[..8]})", desired.RoleName);
    }

    [Fact]
    public void Build_ResourceInOtherSubscription_Throws()
    {
        var ex = Assert.Throws<InfraConfigException>(() =>
            RbacPlan.Build(Config(("x", "vm", "/subscriptions/sub-2/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm")), Sub, Principal));

        Assert.Contains(ex.Errors, e => e.Contains("'x'") && e.Contains("sub-2"));
    }

    [Fact]
    public void DeterministicGuid_IsStable()
    {
        Assert.Equal(RbacPlan.DeterministicGuid("a"), RbacPlan.DeterministicGuid("a"));
        Assert.NotEqual(RbacPlan.DeterministicGuid("a"), RbacPlan.DeterministicGuid("b"));
    }

    [Fact]
    public async Task Run_PutsRoleDefinitionThenAssignments()
    {
        var desired = Desired(VmId, Vm2Id);
        var arm = new FakeArmClient()
            .On(HttpMethod.Put, RoleDefPath(desired), new ArmResponse(201, "{}"))
            .On(HttpMethod.Put, AssignPath(VmId), new ArmResponse(201, "{}"))
            .On(HttpMethod.Put, AssignPath(Vm2Id), new ArmResponse(201, "{}"))
            .On(HttpMethod.Get, ListPath, List());

        await Runner(arm).RunAsync(desired, Sub, Principal, CancellationToken.None);

        var calls = arm.Calls.ToList();
        Assert.Equal((HttpMethod.Put, RoleDefPath(desired)), (calls[0].Method, calls[0].Path));
        Assert.Equal(new[] { AssignPath(VmId), AssignPath(Vm2Id) }.Order(), calls.Skip(1).Take(2).Select(c => c.Path).Order());
        Assert.Equal((HttpMethod.Get, ListPath), (calls[3].Method, calls[3].Path));
        Assert.Contains("\"CustomRole\"", calls[0].Body);
        Assert.Contains("Microsoft.Compute/virtualMachines/start/action", calls[0].Body);
        Assert.Contains($"\"/subscriptions/{Sub}\"", calls[0].Body);
        Assert.Contains("\"ServicePrincipal\"", calls[1].Body);
        Assert.Contains(desired.RoleDefinitionId.ToString(), calls[1].Body);
    }

    [Fact]
    public async Task Run_DeletesOnlyStaleAssignmentsOfOwnRole()
    {
        var desired = Desired(VmId);
        var ownRole = $"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleDefinitions/{desired.RoleDefinitionId}";
        const string removedScope = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/removed";
        var staleId = $"{removedScope}/providers/Microsoft.Authorization/roleAssignments/stale";
        var arm = new FakeArmClient()
            .On(HttpMethod.Put, RoleDefPath(desired), new ArmResponse(201, "{}"))
            .On(HttpMethod.Put, AssignPath(VmId), new ArmResponse(201, "{}"))
            .On(HttpMethod.Get, ListPath, List(
                (staleId, ownRole, removedScope),
                ($"{VmId}/providers/Microsoft.Authorization/roleAssignments/kept", ownRole, VmId.ToUpperInvariant()),
                ($"{removedScope}/providers/Microsoft.Authorization/roleAssignments/other", "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/someone-else", removedScope)))
            .On(HttpMethod.Delete, staleId + Api, new ArmResponse(200, "{}"));

        await Runner(arm).RunAsync(desired, Sub, Principal, CancellationToken.None);

        var delete = Assert.Single(arm.Calls, c => c.Method == HttpMethod.Delete);
        Assert.Equal(staleId + Api, delete.Path);
    }

    [Theory]
    [InlineData("PrincipalNotFound")]
    [InlineData("RoleDefinitionDoesNotExist")]
    public async Task Run_RetriesReplicationErrorsThenSucceeds(string code)
    {
        var desired = Desired(VmId);
        var notYet = new ArmResponse(400, $$"""{ "error": { "code": "{{code}}", "message": "not replicated" } }""");
        var arm = new FakeArmClient()
            .On(HttpMethod.Put, RoleDefPath(desired), new ArmResponse(201, "{}"))
            .On(HttpMethod.Put, AssignPath(VmId), notYet, notYet, new ArmResponse(201, "{}"))
            .On(HttpMethod.Get, ListPath, List());

        await Runner(arm).RunAsync(desired, Sub, Principal, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)], _delays);
    }

    [Fact]
    public async Task Run_ReplicationNeverCompletes_Throws()
    {
        var desired = Desired(VmId);
        var arm = new FakeArmClient()
            .On(HttpMethod.Put, RoleDefPath(desired), new ArmResponse(201, "{}"))
            .On(HttpMethod.Put, AssignPath(VmId), new ArmResponse(400, """{ "error": { "code": "PrincipalNotFound" } }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(arm).RunAsync(desired, Sub, Principal, CancellationToken.None));
        Assert.Equal(6, _delays.Count);
    }

    [Fact]
    public async Task Run_AssignmentExists_TreatedAsSuccess()
    {
        var desired = Desired(VmId);
        var arm = new FakeArmClient()
            .On(HttpMethod.Put, RoleDefPath(desired), new ArmResponse(201, "{}"))
            .On(HttpMethod.Put, AssignPath(VmId), new ArmResponse(409, """{ "error": { "code": "RoleAssignmentExists" } }"""))
            .On(HttpMethod.Get, ListPath, List());

        await Runner(arm).RunAsync(desired, Sub, Principal, CancellationToken.None);
    }

    [Fact]
    public async Task Run_OtherFailure_ThrowsWithStatusAndBody()
    {
        var desired = Desired(VmId);
        var arm = new FakeArmClient().On(HttpMethod.Put, RoleDefPath(desired), new ArmResponse(403, "AuthorizationFailed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(arm).RunAsync(desired, Sub, Principal, CancellationToken.None));

        Assert.Contains("403", ex.Message);
        Assert.Contains("AuthorizationFailed", ex.Message);
    }
}
