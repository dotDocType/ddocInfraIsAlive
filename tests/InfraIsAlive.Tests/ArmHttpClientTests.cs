using System.Net;
using Azure.Core;
using InfraIsAlive.Core.Arm;

namespace InfraIsAlive.Tests;

public sealed class ArmHttpClientTests
{
    private static readonly Uri Endpoint = new("https://management.example.test/");

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class StubCredential : TokenCredential
    {
        public string[]? Scopes { get; private set; }

        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
        {
            Scopes = context.Scopes;
            return new AccessToken("tok", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
            ValueTask.FromResult(GetToken(context, ct));
    }

    private static (ArmHttpClient Client, CapturingHandler Handler, StubCredential Credential) Create(HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new CapturingHandler(status, body);
        var credential = new StubCredential();
        return (new ArmHttpClient(new HttpClient(handler), credential, Endpoint), handler, credential);
    }

    [Fact]
    public async Task Send_UsesConfiguredEndpointAndBearer()
    {
        var (client, handler, credential) = Create();

        await client.SendAsync(HttpMethod.Get, "/subscriptions/s/x?api-version=1", null, CancellationToken.None);

        Assert.Equal("https://management.example.test/subscriptions/s/x?api-version=1", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer tok", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal(["https://management.example.test/.default"], credential.Scopes!);
    }

    [Fact]
    public async Task Send_ReturnsStatusAndBody_OnNonSuccess()
    {
        var (client, _, _) = Create(HttpStatusCode.Conflict, "conflict");

        var response = await client.SendAsync(HttpMethod.Post, "/x/start?api-version=1", null, CancellationToken.None);

        Assert.Equal(new ArmResponse(409, "conflict"), response);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task Send_PostWithoutBody_HasNoContent()
    {
        var (client, handler, _) = Create(HttpStatusCode.Accepted, "");

        await client.SendAsync(HttpMethod.Post, "/x/start?api-version=1", null, CancellationToken.None);

        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task Send_PutWithBody_SendsJson()
    {
        var (client, handler, _) = Create(HttpStatusCode.Created);

        await client.SendAsync(HttpMethod.Put, "/x?api-version=1", """{"a":1}""", CancellationToken.None);

        Assert.Equal("""{"a":1}""", handler.RequestBody);
        Assert.Equal("application/json", handler.Request!.Content!.Headers.ContentType!.MediaType);
    }
}
