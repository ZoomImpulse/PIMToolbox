using System.Text.Json;
using System.Text.Json.Serialization;

namespace PIMToolbox.Core;

public sealed class PimToolboxSettings
{
    public List<TenantConfiguration> Tenants { get; set; } = [];
    public List<string> FavoriteKeys { get; set; } = [];
    public string GlobalJustification { get; set; } = "PIM Quick Action";
    public string WorkStartTime { get; set; } = "08:00";
    public string WorkEndTime { get; set; } = "17:00";
    public string? CloseAction { get; set; }
    public Dictionary<string, DateTimeOffset> ActivationStartTimes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PimToolboxSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.v2.json");
    public string LegacySettingsPath => Path.Combine(DataDirectory, "settings.json");

    public PimToolboxSettingsStore(string? dataDirectory = null) =>
        DataDirectory = dataDirectory ?? AppPaths.DataDirectory;

    public async Task<PimToolboxSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DataDirectory);
        if (File.Exists(SettingsPath))
        {
            await using var input = File.OpenRead(SettingsPath);
            var loaded = await JsonSerializer.DeserializeAsync<PimToolboxSettings>(input, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new PimToolboxSettings();
            ValidateProfileIsolation(loaded);
            return loaded;
        }

        var settings = await MigrateLegacySettingsAsync(cancellationToken).ConfigureAwait(false);
        ValidateProfileIsolation(settings);
        await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    public async Task SaveAsync(PimToolboxSettings settings, CancellationToken cancellationToken = default)
    {
        ValidateProfileIsolation(settings);
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = $"{SettingsPath}.tmp";
        await using (var output = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(output, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private async Task<PimToolboxSettings> MigrateLegacySettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(LegacySettingsPath))
            return new PimToolboxSettings();

        await using var input = File.OpenRead(LegacySettingsPath);
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var settings = new PimToolboxSettings
        {
            GlobalJustification = ReadString(root, "GlobalJustification") ?? "PIM Quick Action",
            WorkStartTime = ReadString(root, "WorkStartTime") ?? "08:00",
            WorkEndTime = ReadString(root, "WorkEndTime") ?? "17:00"
        };

        if (root.TryGetProperty("Tenants", out var tenants) && tenants.ValueKind == JsonValueKind.Array)
        {
            foreach (var tenant in tenants.EnumerateArray())
            {
                var id = ReadString(tenant, "Id");
                var name = ReadString(tenant, "Name");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    settings.Tenants.Add(new TenantConfiguration(name, id, "", "", ""));
            }
        }

        if (root.TryGetProperty("FavoriteRoles", out var favorites) && favorites.ValueKind == JsonValueKind.Array)
        {
            foreach (var favorite in favorites.EnumerateArray())
            {
                var tenantId = ReadString(favorite, "TenantId");
                var id = ReadString(favorite, "Id");
                var resourceId = ReadString(favorite, "ResourceId");
                if (tenantId is not null && id is not null && resourceId is not null)
                    settings.FavoriteKeys.Add(FavoriteKey(tenantId, id, resourceId));
            }
        }

        return settings;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void ValidateProfileIsolation(PimToolboxSettings settings)
    {
        var configuredProfiles = settings.Tenants.Where(EdgeProfileLauncher.IsConfigured).ToArray();
        var invalidProfile = configuredProfiles.FirstOrDefault(tenant =>
            string.IsNullOrWhiteSpace(tenant.EdgeProfileDirectory) ||
            tenant.EdgeProfileDirectory is "." or ".." ||
            tenant.EdgeProfileDirectory.Contains('/') ||
            tenant.EdgeProfileDirectory.Contains('\\'));
        if (invalidProfile is not null)
            throw new InvalidDataException(
                $"Edge profile directory '{invalidProfile.EdgeProfileDirectory}' must be a profile name, not a path.");

        var sharedProfile = configuredProfiles
            .GroupBy(tenant => Path.GetFullPath(Path.Combine(
                tenant.EdgeUserDataDirectory,
                tenant.EdgeProfileDirectory)), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (sharedProfile is not null)
            throw new InvalidDataException(
                $"Multiple tenants use the same Edge profile '{sharedProfile.Key}'. Select a different profile for each tenant.");
    }

    public static string FavoriteKey(PimAssignment assignment) =>
        FavoriteKey(assignment.TenantId, assignment.Id, assignment.ResourceId);

    public static IReadOnlyList<string> ActivationKeys(PimAssignment assignment)
    {
        var keys = new List<string>
        {
            ActivationKey(assignment.TenantId, assignment.Family, assignment.Id,
                assignment.ResourceId, assignment.PrincipalId)
        };
        if (assignment.IsGroupBacked &&
            assignment.UniqueDependencyGroupId is { } groupId &&
            !string.IsNullOrWhiteSpace(assignment.ActivationGroupAccessId))
        {
            keys.Add(ActivationKey(assignment.TenantId, AssignmentFamily.Group,
                assignment.ActivationGroupAccessId, groupId, assignment.PrincipalId));
        }
        return keys;
    }

    private static string ActivationKey(
        string tenantId,
        AssignmentFamily family,
        string id,
        string resourceId,
        string principalId) =>
        $"{tenantId}|{family}|{id}|{resourceId}|{principalId}".ToLowerInvariant();

    private static string FavoriteKey(string tenantId, string id, string resourceId) =>
        $"{tenantId.ToLowerInvariant()}|{id.ToLowerInvariant()}|{resourceId.ToLowerInvariant()}";
}

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "PIMToolbox");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PIMToolbox");
        }
    }

    public static string TokenCacheDirectory => Path.Combine(DataDirectory, "Tokens");
}
