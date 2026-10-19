using System.Text;
using System.Text.Json;
using InfraIsAlive.Core.Configuration;
using InfraIsAlive.Core.Ensure;
using InfraIsAlive.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static EnsureFunction Function(FakeArmClient arm)
    {
        var config = new InfraConfig([Caller], new Dictionary<string, ResourceTypeProfile>(), new Dictionary<string, RegisteredResource>());
        return new EnsureFunction(new EnsureService(config, arm, TimeProvider.System), config, NullLogger<EnsureFunction>.Instance);
    }

    private static HttpRequest Request(string? principal, string body)
    {
        var context = new DefaultHttpContext();
        if (principal is not null) context.Request.Headers[EnsureFunction.PrincipalIdHeader] = principal;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }

    [Fact]
    public async Task Run_NoPrincipal_Returns401()
    {
        var arm = new FakeArmClient();

        var result = await Function(arm).Run(Request(null, """{"names":["x"]}"""), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(arm.Calls);
    }

    [Fact]
    public async Task Run_UnlistedPrincipal_Returns403EvenWithBadBody()
    {
        var arm = new FakeArmClient();

        var result = await Function(arm).Run(Request("22222222-2222-2222-2222-222222222222", "not json"), CancellationToken.None);

        Assert.Equal(403, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Empty(arm.Calls);
    }

    [Fact]
    public async Task Run_AllowedPrincipalBadBody_Returns400()
    {
        var result = await Function(new FakeArmClient()).Run(Request(Caller, "{}"), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<JsonResult>(result).StatusCode);
    }

    [Fact]
    public async Task Run_AllowedPrincipal_Returns200WithResults()
    {
        var result = await Function(new FakeArmClient()).Run(Request(Caller, """{"names":["w"]}"""), CancellationToken.None);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Null(json.StatusCode);
        Assert.Equal(ResourceState.Unknown, Assert.Single(Assert.IsType<EnsureResponse>(json.Value).Results).State);
    }
}
