using Azure.Identity;
using InfraIsAlive.Core.Arm;
using InfraIsAlive.Core.Configuration;
using InfraIsAlive.RbacSync;

const string Usage = """
    Usage: InfraIsAlive.RbacSync --subscription-id <id> --principal-id <object-id> --arm-endpoint <url> --config-dir <dir>

    Creates/updates the InfraIsAlive custom role from the profiles in use, assigns it to the function's
    managed identity on every registered resource, and removes assignments for resources no longer registered.
    Normally run by `azd up` as a postprovision hook.
    """;

string[] required = ["--subscription-id", "--principal-id", "--arm-endpoint", "--config-dir"];
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i + 1 < args.Length; i += 2) options[args[i]] = args[i + 1];

var missing = required.Where(k => !options.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"Missing: {string.Join(", ", missing)}");
    Console.Error.WriteLine(Usage);
    return 2;
}

try
{
    var subscriptionId = options["--subscription-id"];
    var principalId = options["--principal-id"];
    var configDir = options["--config-dir"];

    var config = ConfigLoader.Load(Path.Combine(configDir, "profiles.builtin.json"), Path.Combine(configDir, "infraisalive.json"));
    var desired = RbacPlan.Build(config, subscriptionId, principalId);

    var credential = new ChainedTokenCredential(new AzureDeveloperCliCredential(), new AzureCliCredential());
    using var http = new HttpClient();
    var arm = new ArmHttpClient(http, credential, new Uri(options["--arm-endpoint"]));

    await new RbacSyncRunner(arm, Task.Delay).RunAsync(desired, subscriptionId, principalId, CancellationToken.None);
    return 0;
}
catch (Exception ex) when (ex is InfraConfigException or InvalidOperationException or AuthenticationFailedException or HttpRequestException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
