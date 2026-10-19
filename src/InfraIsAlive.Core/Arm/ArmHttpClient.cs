using System.Net.Http.Headers;
using System.Text;
using Azure.Core;

namespace InfraIsAlive.Core.Arm;

public sealed class ArmHttpClient(HttpClient http, TokenCredential credential, Uri armEndpoint) : IArmClient
{
    private readonly string[] _scopes = [$"{armEndpoint.GetLeftPart(UriPartial.Authority)}/.default"];

    public async Task<ArmResponse> SendAsync(HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(_scopes), ct);

        using var request = new HttpRequestMessage(method, new Uri(armEndpoint, pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        return new ArmResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }
}
