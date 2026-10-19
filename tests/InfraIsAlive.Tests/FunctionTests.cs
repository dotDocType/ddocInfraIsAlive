using System.Text.Json;
using InfraIsAlive.Core.Ensure;
using InfraIsAlive.Functions;

namespace InfraIsAlive.Tests;

public sealed class FunctionTests
{
    private const string Caller = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void IsAllowed_ListedPrincipal_True() =>
        Assert.True(CallerAuthorizer.IsAllowed(Caller.ToUpperInvariant(), [Caller]));

    [Fact]
    public void IsAllowed_UnlistedPrincipal_False() =>
        Assert.False(CallerAuthorizer.IsAllowed("22222222-2222-2222-2222-222222222222", [Caller]));

    [Fact]
    public void IsAllowed_EmptyAllowlist_DeniesEveryone() =>
        Assert.False(CallerAuthorizer.IsAllowed(Caller, []));

    [Fact]
    public void IsAllowed_NullPrincipal_False() =>
        Assert.False(CallerAuthorizer.IsAllowed(null, [Caller]));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"names\":[]}")]
    [InlineData("{\"names\":[\"  \"]}")]
    [InlineData("{\"names\":\"x\"}")]
    [InlineData("{\"names\":[1]}")]
    [InlineData("[\"x\"]")]
    public void TryParse_Rejects(string body)
    {
        Assert.False(EnsureRequestParser.TryParse(body, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_Accepts()
    {
        Assert.True(EnsureRequestParser.TryParse("""{"names":["x","z"]}""", out var names, out var error));
        Assert.Equal(["x", "z"], names);
        Assert.Null(error);
    }

    [Fact]
    public void Response_SerializesCamelCaseStringEnumsAndOmitsNulls()
    {
        var at = new DateTimeOffset(2026, 10, 19, 23, 30, 1, TimeSpan.Zero);
        var response = new EnsureResponse([new EnsureResult("x", ResourceState.Online, false, "Ready", null, at)]);

        var json = JsonSerializer.Serialize(response, EnsureFunction.ResponseJson);

        Assert.Contains("\"results\":[", json);
        Assert.Contains("\"name\":\"x\"", json);
        Assert.Contains("\"state\":\"Online\"", json);
        Assert.Contains("\"startTriggered\":false", json);
        Assert.Contains("\"rawState\":\"Ready\"", json);
        Assert.Contains("\"checkedAt\":\"2026-10-19T23:30:01+00:00\"", json);
        Assert.DoesNotContain("message", json);
    }
}
