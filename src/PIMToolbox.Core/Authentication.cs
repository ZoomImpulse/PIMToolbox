using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace PIMToolbox.Core;

public sealed record AuthenticationProof(
    TenantIdentity Identity,
    string GraphUserId,
    string GraphDisplayName,
    string AccessToken);

public sealed class TenantAuthenticationService
{
    private static readonly string[] GraphScopes =
    [
        "User.Read",
        "Group.Read.All",
        "RoleManagement.ReadWrite.Directory",
        "PrivilegedAccess.ReadWrite.AzureADGroup"
    ];

    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;

    public TenantAuthenticationService(HttpClient httpClient, string? cacheDirectory = null)
    {
        _httpClient = httpClient;
        _cacheDirectory = cacheDirectory ?? AppPaths.TokenCacheDirectory;
    }

    public async Task<AuthenticationProof> SignInAndVerifyAsync(
        TenantConfiguration tenant,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenant);
        Directory.CreateDirectory(_cacheDirectory);

        var application = PublicClientApplicationBuilder
            .Create(tenant.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenant.TenantId}")
            .WithRedirectUri("http://localhost")
            .Build();

        var cacheName = $"PIMToolbox-{tenant.TenantId.ToLowerInvariant()}.msalcache";
        var cacheProperties = new StorageCreationPropertiesBuilder(cacheName, _cacheDirectory)
            .WithMacKeyChain("PIMToolbox", $"PIMToolbox-{tenant.TenantId}")
            .Build();
        var cacheHelper = await MsalCacheHelper.CreateAsync(cacheProperties).ConfigureAwait(false);
        cacheHelper.RegisterCache(application.UserTokenCache);

        AuthenticationResult result;
        var accounts = await application.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault(candidate =>
            tenant.ExpectedUsername is null ||
            string.Equals(candidate.Username, tenant.ExpectedUsername, StringComparison.OrdinalIgnoreCase));

        try
        {
            result = account is null
                ? await AcquireInteractiveAsync(application, tenant, cancellationToken).ConfigureAwait(false)
                : await application.AcquireTokenSilent(GraphScopes, account)
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            result = await AcquireInteractiveAsync(application, tenant, cancellationToken).ConfigureAwait(false);
        }

        if (!Guid.TryParse(result.TenantId, out var returnedTenant) ||
            !Guid.TryParse(tenant.TenantId, out var configuredTenant) ||
            returnedTenant != configuredTenant)
            throw new InvalidOperationException("The signed-in account belongs to a different tenant than the configured tenant.");

        var username = result.Account?.Username;
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Microsoft sign-in did not return an account username.");

        if (tenant.ExpectedUsername is not null &&
            !string.Equals(username, tenant.ExpectedUsername, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The signed-in account does not match the expected account configured for this tenant.");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/me?$select=id,displayName,userPrincipalName");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.AccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var graphPayload = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = graphPayload.RootElement;
        var graphId = root.GetProperty("id").GetString();
        var graphName = root.TryGetProperty("displayName", out var name) ? name.GetString() : null;
        var graphUsername = root.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null;

        if (string.IsNullOrWhiteSpace(graphId))
            throw new InvalidDataException("The read-only Microsoft Graph /me response did not include a user id.");
        if (graphUsername is not null && !string.Equals(graphUsername, username, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Microsoft Graph account does not match the account returned by sign-in.");

        return new AuthenticationProof(
            new TenantIdentity(configuredTenant.ToString(), graphId, graphUsername ?? username, graphName ?? username),
            graphId,
            graphName ?? username,
            result.AccessToken);
    }

    private static async Task<AuthenticationResult> AcquireInteractiveAsync(
        IPublicClientApplication application,
        TenantConfiguration tenant,
        CancellationToken cancellationToken)
    {
        var options = new SystemWebViewOptions
        {
            OpenBrowserAsync = authorizationUri =>
                EdgeProfileLauncher.IsConfigured(tenant)
                    ? EdgeProfileLauncher.LaunchAsync(authorizationUri, tenant)
                    : EdgeProfileLauncher.LaunchInDefaultBrowserAsync(authorizationUri)
        };

        return await application.AcquireTokenInteractive(GraphScopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithSystemWebViewOptions(options)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateTenant(TenantConfiguration tenant)
    {
        if (!Guid.TryParse(tenant.TenantId, out _))
            throw new ArgumentException("TenantId must be a tenant GUID.", nameof(tenant));
        if (!Guid.TryParse(tenant.ClientId, out _))
            throw new ArgumentException("ClientId must be an application GUID.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(tenant.ExpectedUsername))
            throw new ArgumentException("Configure the expected signed-in username for each tenant.", nameof(tenant));
        var hasEdgePath = !string.IsNullOrWhiteSpace(tenant.EdgeExecutable);
        var hasProfile = !string.IsNullOrWhiteSpace(tenant.EdgeUserDataDirectory);
        if (hasEdgePath != hasProfile)
            throw new ArgumentException("Configure both EdgeExecutable and EdgeUserDataDirectory, or leave both empty to use the system browser.", nameof(tenant));
        if (hasEdgePath && !File.Exists(tenant.EdgeExecutable))
            throw new FileNotFoundException("The configured Microsoft Edge executable does not exist.", tenant.EdgeExecutable);
        if (hasProfile && !Path.IsPathFullyQualified(tenant.EdgeUserDataDirectory))
            throw new ArgumentException("EdgeUserDataDirectory must be an explicit absolute path.", nameof(tenant));
        if (hasProfile && string.IsNullOrWhiteSpace(tenant.EdgeProfileDirectory))
            throw new ArgumentException("EdgeProfileDirectory cannot be empty.", nameof(tenant));
    }
}

public static class EdgeProfileLauncher
{
    public static bool IsConfigured(TenantConfiguration tenant) =>
        !string.IsNullOrWhiteSpace(tenant.EdgeExecutable) &&
        !string.IsNullOrWhiteSpace(tenant.EdgeUserDataDirectory);

    public static ProcessStartInfo CreateStartInfo(Uri authorizationUri, TenantConfiguration tenant)
    {
        if (!IsConfigured(tenant))
            throw new ArgumentException("An Edge executable and user-data directory are required.", nameof(tenant));

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(tenant.EdgeExecutable),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--user-data-dir={Path.GetFullPath(tenant.EdgeUserDataDirectory)}");
        startInfo.ArgumentList.Add($"--profile-directory={tenant.EdgeProfileDirectory}");
        startInfo.ArgumentList.Add(authorizationUri.AbsoluteUri);
        return startInfo;
    }

    public static Task LaunchAsync(Uri authorizationUri, TenantConfiguration tenant)
    {
        var userData = Path.GetFullPath(tenant.EdgeUserDataDirectory);
        Directory.CreateDirectory(userData);

        var startInfo = CreateStartInfo(authorizationUri, tenant);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("Edge profile launching is supported only on Windows and macOS.");

        var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Microsoft Edge could not be started.");
        process.Dispose();
        return Task.CompletedTask;
    }

    public static Task LaunchInDefaultBrowserAsync(Uri authorizationUri)
    {
        var process = Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        if (process is null)
            throw new InvalidOperationException("The system browser could not be started.");
        process.Dispose();
        return Task.CompletedTask;
    }
}
