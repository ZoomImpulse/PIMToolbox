using System.Text.Json;
using PIMToolbox.Core;

var configPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(AppPaths.DataDirectory, "settings.v2.json");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"No settings file found: {configPath}");
    Console.Error.WriteLine("Create it using the tenant profile fields described in README.md.");
    return 2;
}

var settings = await JsonSerializer.DeserializeAsync<PocSettings>(
    File.OpenRead(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
var tenants = settings?.Tenants ?? [];
if (tenants.Length == 0)
{
    Console.Error.WriteLine("Configure at least one tenant in the settings file.");
    return 2;
}

for (var i = 0; i < tenants.Length; i++)
    Console.WriteLine($"{i + 1}. {tenants[i].Name} ({tenants[i].TenantId})");
Console.Write("Tenant number to test: ");
if (!int.TryParse(Console.ReadLine(), out var selection) || selection < 1 || selection > tenants.Length)
{
    Console.Error.WriteLine("Select a tenant number from the list.");
    return 2;
}

try
{
    using var http = new HttpClient();
    var auth = new TenantAuthenticationService(http);
    var proof = await auth.SignInAndVerifyAsync(tenants[selection - 1]);
    Console.WriteLine($"PASS: Entra tenant {proof.Identity.TenantId} returned account {proof.Identity.Username}.");
    Console.WriteLine($"PASS: Read-only Graph /me returned {proof.GraphDisplayName} ({proof.GraphUserId}).");
    Console.WriteLine("The POC did not read authorization codes or browser cookies; MSAL persists its device-local token cache using OS protection.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Authentication proof failed: {error.Message}");
    return 1;
}

internal sealed record PocSettings(TenantConfiguration[] Tenants);
