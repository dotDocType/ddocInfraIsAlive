using InfraIsAlive.Core.Configuration;

namespace InfraIsAlive.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private const string ValidId = "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-1";

    private readonly string _dir = Directory.CreateTempSubdirectory("infraisalive-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static string Profile(string actions = "[\"a\",\"b\",\"c\"]", string stateMap = "{\"Ready\":\"Online\"}", string statusField = "$.properties.state") => $$"""
        {
          "ApiVersion": "2024-01-01",
          "StatusPath": "",
          "StatusField": "{{statusField}}",
          "StartAction": "start",
          "StateMap": {{stateMap}},
          "RequiredActions": {{actions}}
        }
        """;

    [Fact]
    public void Load_MergesBuiltinAndAdopter()
    {
        var builtin = Write("builtin.json", $$"""{ "ResourceTypes": { "vm": {{Profile()}} } }""");
        var adopter = Write("adopter.json", $$"""
            {
              "AllowedCallerObjectIds": ["11111111-1111-1111-1111-111111111111"],
              "ResourceTypes": { "redis": {{Profile()}} },
              "Resources": { "x": { "Type": "vm", "ResourceId": "{{ValidId}}" } }
            }
            """);

        var cfg = ConfigLoader.Load(builtin, adopter);

        Assert.Contains("vm", cfg.ResourceTypes.Keys);
        Assert.Contains("redis", cfg.ResourceTypes.Keys);
        Assert.Equal("vm", cfg.Resources["X"].Type);
        Assert.Equal(["11111111-1111-1111-1111-111111111111"], cfg.AllowedCallerObjectIds);
    }

    [Fact]
    public void Load_AdopterProfileReplacesBuiltinEntirely()
    {
        var builtin = Write("builtin.json", $$"""{ "ResourceTypes": { "vm": {{Profile("[\"a\",\"b\",\"c\"]")}} } }""");
        var adopter = Write("adopter.json", $$"""{ "ResourceTypes": { "vm": {{Profile("[\"a\"]")}} }, "Resources": {} }""");

        var cfg = ConfigLoader.Load(builtin, adopter);

        Assert.Equal(["a"], cfg.ResourceTypes["vm"].RequiredActions);
    }

    [Fact]
    public void Load_StateMapIsCaseInsensitive()
    {
        var builtin = Write("builtin.json", $$"""{ "ResourceTypes": { "pg": {{Profile()}} } }""");
        var adopter = Write("adopter.json", """{ "Resources": {} }""");

        var cfg = ConfigLoader.Load(builtin, adopter);

        Assert.Equal(MappedState.Online, cfg.ResourceTypes["pg"].StateMap["READY"]);
    }

    [Fact]
    public void Load_InvalidConfig_ListsAllErrors()
    {
        var builtin = Write("builtin.json", $$"""
            {
              "ResourceTypes": {
                "ok": {{Profile()}},
                "p": {{Profile(stateMap: "{}")}},
                "q": {{Profile(stateMap: "{\"Ready\":\"Sleeping\"}")}},
                "r": {{Profile(statusField: "")}}
              }
            }
            """);
        var adopter = Write("adopter.json", $$"""
            {
              "Resources": {
                "a": { "Type": "nope", "ResourceId": "{{ValidId}}" },
                "b": { "Type": "ok", "ResourceId": "not-an-id" }
              }
            }
            """);

        var ex = Assert.Throws<InfraConfigException>(() => ConfigLoader.Load(builtin, adopter));

        Assert.Equal(5, ex.Errors.Count);
        Assert.Contains(ex.Errors, e => e.Contains("'a'"));
        Assert.Contains(ex.Errors, e => e.Contains("'b'"));
        Assert.Contains(ex.Errors, e => e.Contains("'p'"));
        Assert.Contains(ex.Errors, e => e.Contains("'q'"));
        Assert.Contains(ex.Errors, e => e.Contains("'r'"));
    }

    [Fact]
    public void Load_MalformedStatusField_ListsError()
    {
        var builtin = Write("builtin.json", $$"""{ "ResourceTypes": { "bad": {{Profile(statusField: "$.properties[?(@.x ==")}} } }""");
        var adopter = Write("adopter.json", """{ "Resources": {} }""");

        var ex = Assert.Throws<InfraConfigException>(() => ConfigLoader.Load(builtin, adopter));

        Assert.Contains(ex.Errors, e => e.Contains("'bad'") && e.Contains("StatusField"));
    }

    [Fact]
    public void Load_MissingAdopterFile_ThrowsWithPath()
    {
        var builtin = Write("builtin.json", """{ "ResourceTypes": {} }""");
        var missing = Path.Combine(_dir, "does-not-exist.json");

        var ex = Assert.Throws<InfraConfigException>(() => ConfigLoader.Load(builtin, missing));

        Assert.Contains(missing, ex.Message);
    }
}
