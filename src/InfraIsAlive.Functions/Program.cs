using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using InfraIsAlive.Core.Arm;
using InfraIsAlive.Core.Configuration;
using InfraIsAlive.Core.Ensure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Application Insights is optional (deployment parameter); export telemetry only when it is configured.
var telemetry = builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    telemetry.UseAzureMonitorExporter();

string Required(string key) =>
    builder.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing required setting '{key}' (app setting '{key.Replace(":", "__")}').");

var armEndpoint = new Uri(Required("InfraIsAlive:ArmEndpoint"));
var managedIdentityClientId = Required("InfraIsAlive:ManagedIdentityClientId");

var configDir = Path.Combine(AppContext.BaseDirectory, "config");
var config = ConfigLoader.Load(
    Path.Combine(configDir, "profiles.builtin.json"),
    Path.Combine(configDir, "infraisalive.json"));

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenCredential>(
    new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId)));
builder.Services.AddHttpClient(nameof(ArmHttpClient), c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IArmClient>(sp => new ArmHttpClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ArmHttpClient)),
    sp.GetRequiredService<TokenCredential>(),
    armEndpoint));
builder.Services.AddSingleton<EnsureService>();

builder.Build().Run();
