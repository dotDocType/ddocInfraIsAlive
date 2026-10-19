using InfraIsAlive.Core.Configuration;
using InfraIsAlive.Core.Evaluation;

namespace InfraIsAlive.Tests;

public sealed class StateEvaluationTests
{
    internal static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    internal static string Fixture(string name) => File.ReadAllText(FixturePath(name));

    internal static InfraConfig BuiltinOnly()
    {
        var adopter = Path.Combine(Path.GetTempPath(), $"infraisalive-empty-{Guid.NewGuid():N}.json");
        File.WriteAllText(adopter, """{ "Resources": {} }""");
        try
        {
            return ConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "profiles.builtin.json"), adopter);
        }
        finally
        {
            File.Delete(adopter);
        }
    }

    [Theory]
    [InlineData("vm-instanceview-running.json", "vm", "PowerState/running", MappedState.Online)]
    [InlineData("vm-instanceview-deallocated.json", "vm", "PowerState/deallocated", MappedState.Stopped)]
    [InlineData("postgres-ready.json", "postgres-flexible", "Ready", MappedState.Online)]
    [InlineData("postgres-stopped.json", "postgres-flexible", "Stopped", MappedState.Stopped)]
    [InlineData("containerapp-running.json", "container-app", "Running", MappedState.Online)]
    [InlineData("containerapp-stopped.json", "container-app", "Stopped", MappedState.Stopped)]
    public void BuiltinProfile_ExtractsAndMaps(string fixture, string type, string expectedRaw, MappedState expected)
    {
        var profile = BuiltinOnly().ResourceTypes[type];

        var raw = StateExtractor.Extract(Fixture(fixture), profile.StatusField);

        Assert.Equal(expectedRaw, raw);
        Assert.Equal(expected, StateMapper.Map(profile, raw));
    }

    [Fact]
    public void Extract_NoMatch_ReturnsNull()
    {
        var profile = BuiltinOnly().ResourceTypes["vm"];

        Assert.Null(StateExtractor.Extract(Fixture("vm-instanceview-no-powerstate.json"), profile.StatusField));
    }

    [Fact]
    public void Map_UnknownRawValue_ReturnsNull()
    {
        Assert.Null(StateMapper.Map(BuiltinOnly().ResourceTypes["postgres-flexible"], "Hibernating"));
    }

    [Fact]
    public void Map_NullRaw_ReturnsNull()
    {
        Assert.Null(StateMapper.Map(BuiltinOnly().ResourceTypes["postgres-flexible"], null));
    }
}
