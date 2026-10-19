using System.Text.Json;
using System.Text.Json.Serialization;
using InfraIsAlive.Core.Configuration;
using InfraIsAlive.Core.Ensure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace InfraIsAlive.Functions;

public sealed record EnsureResponse(IReadOnlyList<EnsureResult> Results);

public sealed class EnsureFunction(EnsureService service, InfraConfig config, ILogger<EnsureFunction> logger)
{
    /// <summary>Injected by App Service authentication (Easy Auth); clients cannot set it themselves.</summary>
    public const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    public static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [Function("ensure")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ensure")] HttpRequest req,
        CancellationToken ct)
    {
        var principalId = req.Headers[PrincipalIdHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(principalId))
        {
            logger.LogWarning("Rejected call without an authenticated principal.");
            return new UnauthorizedResult();
        }

        if (!CallerAuthorizer.IsAllowed(principalId, config.AllowedCallerObjectIds))
        {
            logger.LogWarning("Rejected caller {PrincipalId}: not on the allowlist.", principalId);
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        using var reader = new StreamReader(req.Body);
        if (!EnsureRequestParser.TryParse(await reader.ReadToEndAsync(ct), out var names, out var error))
            return new JsonResult(new { error }, ResponseJson) { StatusCode = StatusCodes.Status400BadRequest };

        var results = await service.EnsureAsync(names, ct);
        foreach (var r in results)
        {
            logger.LogInformation(
                "Ensure {Name} for {PrincipalId}: {State} (raw {RawState}, startTriggered {StartTriggered}) {Message}",
                r.Name, principalId, r.State, r.RawState, r.StartTriggered, r.Message);
        }

        return new JsonResult(new EnsureResponse(results), ResponseJson);
    }
}
