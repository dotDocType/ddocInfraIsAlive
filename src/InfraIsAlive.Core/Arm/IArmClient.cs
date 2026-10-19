namespace InfraIsAlive.Core.Arm;

public sealed record ArmResponse(int StatusCode, string Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>Sends raw requests to Azure Resource Manager. Non-success statuses are returned, not thrown.</summary>
public interface IArmClient
{
    Task<ArmResponse> SendAsync(HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken ct);
}
