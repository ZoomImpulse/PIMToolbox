using System.Runtime.InteropServices;
using System.Text.Json;

namespace PIMToolbox.Desktop;

public sealed record EdgeProfileOption(
    string DisplayName,
    string EdgeExecutable,
    string UserDataDirectory,
    string ProfileDirectory,
    bool IsSystemBrowser = false)
{
    public static EdgeProfileOption SystemBrowser { get; } =
        new("Use system browser", "", "", "", true);
}

public static class EdgeProfileDiscovery
{
    public static IReadOnlyList<EdgeProfileOption> Discover()
    {
        var executable = FindEdgeExecutable();
        var profiles = new List<EdgeProfileOption> { EdgeProfileOption.SystemBrowser };
        foreach (var userDataDirectory in GetUserDataDirectories())
            profiles.AddRange(DiscoverFromUserDataDirectory(userDataDirectory, executable));
        return profiles;
    }

    public static IReadOnlyList<EdgeProfileOption> DiscoverFromUserDataDirectory(
        string userDataDirectory,
        string edgeExecutable = "")
    {
        if (!Directory.Exists(userDataDirectory))
            return [];

        var profileNames = ReadProfileNames(userDataDirectory);
        var profiles = new List<EdgeProfileOption>();
        foreach (var (directory, displayName, userName) in profileNames)
        {
            var label = string.IsNullOrWhiteSpace(userName)
                ? displayName
                : $"{displayName} — {userName}";
            profiles.Add(new EdgeProfileOption(
                $"{label} ({directory})",
                edgeExecutable,
                Path.GetFullPath(userDataDirectory),
                directory));
        }

        return profiles;
    }

    public static string FindEdgeExecutable()
    {
        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Applications", "Microsoft Edge.app", "Contents", "MacOS", "Microsoft Edge"));
        }

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static IEnumerable<string> GetUserDataDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.Combine(home, "Library", "Application Support", "Microsoft Edge");
        }
    }

    private static IReadOnlyList<(string Directory, string DisplayName, string UserName)> ReadProfileNames(
        string userDataDirectory)
    {
        var localStatePath = Path.Combine(userDataDirectory, "Local State");
        if (File.Exists(localStatePath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(localStatePath));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("profile", out var profile) &&
                    profile.ValueKind == JsonValueKind.Object &&
                    profile.TryGetProperty("info_cache", out var infoCache) &&
                    infoCache.ValueKind == JsonValueKind.Object)
                {
                    var profiles = new List<(string Directory, string DisplayName, string UserName)>();
                    foreach (var item in infoCache.EnumerateObject())
                    {
                        if (item.Value.ValueKind != JsonValueKind.Object)
                            continue;
                        if (Path.GetFileName(item.Name) != item.Name || item.Name is "." or "..")
                            continue;
                        var name = GetString(item.Value, "name");
                        var userName = GetString(item.Value, "user_name");
                        if (!string.IsNullOrWhiteSpace(name) &&
                            Directory.Exists(Path.Combine(userDataDirectory, item.Name)))
                            profiles.Add((item.Name, name, userName));
                    }

                    if (profiles.Count > 0)
                        return profiles;
                }
            }
            catch (JsonException)
            {
            }
        }

        return Directory.EnumerateDirectories(userDataDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "Preferences")))
            .Select(directory =>
            {
                var profileDirectory = Path.GetFileName(directory);
                var displayName = ReadDisplayName(Path.Combine(directory, "Preferences")) ?? profileDirectory;
                return (profileDirectory, displayName, "");
            })
            .ToArray();
    }

    private static string? ReadDisplayName(string preferencesPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(preferencesPath));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("profile", out var profile) &&
                profile.ValueKind == JsonValueKind.Object)
                return GetString(profile, "name");
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
